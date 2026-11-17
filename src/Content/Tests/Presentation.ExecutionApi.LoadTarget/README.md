# Presentation.ExecutionApi.LoadTarget

Load and stress testing for the Execution API. This project is a console host that runs the **real**
Execution API on **real Kestrel** with two substitutions, so k6 can hammer it without an Azure OpenAI
bill or an Entra tenant. The k6 scenarios beside it drive that host; one PowerShell script runs them.

```powershell
./scripts/load/Invoke-ExecutionApiLoad.ps1                       # smoke: every scenario, about a minute each
./scripts/load/Invoke-ExecutionApiLoad.ps1 -Profile baseline     # the reference measurement
./scripts/load/Invoke-ExecutionApiLoad.ps1 -Profile stress -ConfirmHeavy
```

## What is real and what is not

| Part | Under load | Why |
|------|-----------|-----|
| HTTP pipeline, Kestrel, routing, controllers, MediatR | Real | This is what is under test |
| JWT validation | Real, against a locally generated RSA key | Every request pays the true validation cost; only where the key comes from changes |
| Rate limiters, per-owner caps, dispatch queue, streaming | Real, with the shipped defaults | The limits are part of what is under test |
| Bundle staging, conversation store, audit | Real, under a throwaway state folder | File and SQLite I/O is a candidate bottleneck |
| The model | Simulated (`Tests.AI.Fakes/SimulatedModelChatClient`) | Paced token-by-token streaming with a tunable time to first token, token rate, length and transient-failure rate |
| Workflow LlmCall agent | A skill the target writes under the step's deployment name | The host resolves a step's `modelDeploymentKey` as a skill id, so without it every submitted LLM step fails (see `LoadTargetEnvironment.WorkflowAgent`) |
| OpenTelemetry export | Off unless `-WithTelemetry` | So exporter cost is measured separately, not mixed into every run |

`LoadTargetHost` swaps exactly two services: `IChatClientFactory` (the simulated model) and the JwtBearer
`ConfigurationManager` (trusts `LocalTokenIssuer`'s key instead of Entra's metadata endpoint). Everything
else is the host's own composition. `LoadTargetEnvironment` sets the few config values a load run needs
(bundle execution and workflow submission on, authentication required, the issuer's tenant and client
ids) as environment variables, which the host reads last.

### Model profiles (`-Model`)

| Profile | Time to first token | Tokens per second | Response tokens | Failures |
|---------|--------------------|-------------------|-----------------|----------|
| `instant` | 0 | unthrottled | 20 | none |
| `realistic` (default) | 600 ms | 40 | 150 | none |
| `slow` | 3 s | 20 | 300 | none |
| `flaky` | as realistic | | | 5% transient 429s |

`instant` measures pure harness overhead. `-FailureRate` overrides any profile's failure rate; injected
failures are `HttpRequestException` with status 429, which `DefaultProviderErrorClassifier` treats as
transient, so retry and circuit-breaker behave as they would against a throttling provider.

## Scenarios (`k6/scenarios/`)

| Scenario | Load shape | What it proves |
|----------|-----------|----------------|
| `poll-storm` | Steady arrival rate of status polls | Per-request pipeline overhead (auth, scope, limiter, MediatR) with almost no work behind it |
| `submit-burst` | Many callers start background runs and wait | Dispatch queue depth and time-to-start with 4 dispatch slots |
| `stream-soak` | Many callers hold live streams back to back | Cost of many simultaneous streams; bundle streams have no host-wide cap |
| `register-burst` | Many callers register and delete bundles | Archive staging and file I/O under concurrency |
| `workflow-runs` | Many callers run a one-step LLM workflow and watch its progress stream | The workflow path and its host-wide stream cap of 64 (503s above it are expected) |
| `noisy-neighbour` | One caller floods the queue beside many light callers | Fairness: dispatch is first in, first out across callers |
| `limits-hold` | Single callers exceed each documented limit once | Every limit answers 429 or 400, never 5xx |
| `spike` | Light load, a jump to three times the ramp level (at most 300 VUs), back to light | Recovery: `run_end_to_end{phase:recovery}` against `{phase:before}` |
| `soak` | Steady background runs and streams for the whole profile (60 min under `soak`) | Leaks: `{phase:late}` against `{phase:early}`, plus GC heap in `counters.csv` |
| `tool-invoke` | Many callers invoke `echo_calculate` directly (needs `-EnableToolInvocation`) | The direct-invocation path's overhead |

Every VU is its own caller with its own token, so each sits in its own rate-limit partition and the
scenarios measure the host, not the per-caller limiter. Only `limits-hold` and the heavy caller in
`noisy-neighbour` cross limits on purpose.

### Profiles (`-Profile`)

| Profile | VUs | Ramp to | Hold | Notes |
|---------|-----|---------|------|-------|
| `smoke` (default) | 5 | 10 | 30 s | Proves every scenario works end to end |
| `baseline` | 50 | 100 | 3 min | The reference numbers |
| `stress` | 100 | 400 | 5 min | Needs `-ConfirmHeavy` |
| `soak` | 50 | 50 | 60 min | Runs only `soak`; needs `-ConfirmHeavy` |

The numbers live in `k6/lib/config.js`, so a profile means the same thing in every scenario.

## Pass and fail

k6's own `http_req_failed` counts every 4xx as a failure, which is wrong here: a 429 from the limiter is
the host working. Every response is classified instead (`k6/lib/metrics.js`), and the thresholds
(`k6/lib/thresholds.js`) are written against those counters:

- `server_errors` (any 5xx, or no answer at all) must be **zero** in every scenario.
- `unexpected_status` (anything neither success nor a documented refusal) must be **zero**.
- `rate_limited` (429) must be zero wherever a scenario stays inside the limits.
- `run_succeeded` must stay above 99%. It reads both the run status and
  `result.conversationSucceeded`, because a bundle run whose agent turn failed still reports
  `Succeeded` at the run level.
- Latency bounds are deliberately generous: they catch a collapse, not a slowdown. After a baseline
  run, tighten them to just above the baseline so a regression fails.

To prove the thresholds can fail, run with the model failing every call:

```powershell
./scripts/load/Invoke-ExecutionApiLoad.ps1 -Scenario submit-burst -FailureRate 1
```

## Reading the results

Each run writes `artifacts/load/<timestamp>-<profile>/<scenario>/` (gitignored):

| File | Contents |
|------|----------|
| `report.html` | k6 web-dashboard export: request rate, latency percentiles and custom metrics over time |
| `summary.json` | End-of-test summary, for comparing one run with another |
| `k6.log` | Console output, including each threshold's verdict |
| `counters.csv` | Once a second: thread-pool queue length and thread count, GC heap and pause time, allocation rate, Kestrel connections, rate-limiter leases |
| `target.log` | The host's console output (warnings and above) |
| `fixtures/` | The tokens, bundle and target description the run used; worthless once the target has stopped, because its signing key is gone |

The useful custom metrics: `run_queue_wait` (server-side time a background run sat queued),
`run_execution`, `run_end_to_end`, `stream_first_byte` (did a streamed run start promptly) and
`stream_duration`.

## Machine safety

Every protection applies only to the processes the runner starts; nothing else on the machine changes.

- It refuses to start while ComfyUI is running (port 8188, or a process started from a `ComfyUI`
  folder) or with less than 6 GB of free memory.
- The build, the target, k6 and dotnet-counters run at below-normal priority on at most two thirds of
  the cores, so the desktop always keeps the rest. The target and k6 are told that core count, so their
  thread pools are sized to it.
- A watchdog stops the whole run if free memory falls below 4 GB or the run passes its overall limit
  (checked every 2 seconds), or if ComfyUI starts (checked every 15 seconds). Scenarios not yet run are
  reported as `not run`.
- Time limits, in minutes:

  | Profile | Expected | Whole run | Each scenario |
  |---|---|---|---|
  | `smoke` | ~15 | 30 | 10 |
  | `baseline` | ~40 | 60 | 20 |
  | `stress` | ~90 | 120 | 30 |
  | `soak` | ~65 | 80 | 90 (the run limit stops it first) |

  Each scenario waits up to 5 minutes after its last stage for runs already started, so their longest
  waits are recorded rather than cut off, and `spike` waits for its backlog to drain before it measures
  recovery.

- It starts a fresh target per scenario and always stops it, and will not run `stress` or `soak`
  without `-ConfirmHeavy`.

Load generator and target share one machine, so k6 itself can become the bottleneck: if k6's CPU sits
near 100%, the numbers describe k6, not the API.

## Scenario rules

- **Ask a new question every run.** `startBundleRun` does this for you. The chat response cache answers
  a repeated question from memory, so a fixed question makes every run after a caller's first measure
  the cache, not the harness.
- **Pace every loop.** End each iteration with `paceIteration(startedAt)`, which holds it to at least
  4 seconds. An iteration that ends early (a refused start, the `instant` model) otherwise repeats at
  once, and the scenario measures its own throttling instead of the API.

## Prerequisites

```powershell
winget install k6 --source winget
dotnet tool install -g dotnet-counters
```

`dotnet-counters` is optional; without it the run skips `counters.csv`.

## Running the target by hand

```powershell
dotnet run -c Release --project src/Content/Tests/Presentation.ExecutionApi.LoadTarget -- --out ./artifacts/load/manual --model realistic
$env:FIXTURES = "./artifacts/load/manual"; $env:PROFILE = "smoke"
k6 run src/Content/Tests/Presentation.ExecutionApi.LoadTarget/k6/scenarios/stream-soak.js
```

The target prints `LOAD TARGET READY <url>` once it is listening, then waits for Ctrl+C. Started
without `--out`, it prints its options and exits.
