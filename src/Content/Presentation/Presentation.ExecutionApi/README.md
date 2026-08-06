# Presentation.ExecutionApi

The lean, headless ASP.NET Core host that lets an external system **inject and run an agent the harness did not write**. A caller uploads a bundle -- a zip containing an `AGENT.md`, its nested `skills/*/SKILL.md`, and any plugin manifests -- gets back a short-lived handle, and starts runs against it. Each run is a bounded multi-turn conversation executed by an *ephemeral* agent that never enters the host's persistent agent registry and whose skills never enter the global skill pool.

It is a composition root, exactly like `Presentation.AgentHub`: `builder.Services.GetServices()` wires every Application + Infrastructure layer, then `AddExecutionApiServices()` adds this host's own controllers, authentication scheme, and rate limiters. Unlike the agent hub it launches no SPA, registers no HealthChecks UI, and serves **no Swagger endpoint** -- the machine-readable contract is the checked-in spec at `documentation/onboarding/assets/openapi/bundle-api.yaml`.

**Audience note.** This README is for engineers working *on* the host. The integration guide for callers -- quickstart, endpoint semantics, error handling, client recipes -- is Chapter 17 of the developer guide (`documentation/onboarding/17-bundle-api.html`). Keep the two in sync when the contract changes.

## Architecture Context

```
┌──────────────────────────────────────────────────────────────────────────────┐
│  Presentation.ExecutionApi  (Composition Root)                                  │
│                                                                              │
│  Program.cs → GetServices(includeHealthChecksUI: false)                      │
│             → AddExecutionApiServices(configuration)                            │
│             → AddExecutionApiEvaluation(configuration)  ← opt-in, fail-closed │
│             → UseDefaultServiceProvider(ApplyValidationPolicy)  ← all envs   │
│                                                                              │
│  Middleware: SecurityHeaders → GlobalException → [HSTS/HTTPS] → Routing      │
│              → Authentication → Authorization → RateLimiter → Controllers    │
│                                                                              │
│  ┌────────────────────────────────────────────────────────────────────────┐ │
│  │  BundlesController  [Authorize]  /api/bundles                          │ │
│  │                                                                        │ │
│  │  POST   /                        → RegisterBundleCommand               │ │
│  │  POST   /{handle}/runs           → RunBundleCommand   ← envelope bound  │ │
│  │           optional conversationId → run continues a durable transcript  │ │
│  │  GET    /{handle}/runs/{jobId}   → GetBundleRunQuery                   │ │
│  │  GET    /{handle}/runs/{jobId}/stream → BundleRunStreamer (SSE)        │ │
│  │  DELETE /{handle}                → DeleteBundleCommand                 │ │
│  └───────────────────────────────┬────────────────────────────────────────┘ │
│  ┌───────────────────────────────┴────────────────────────────────────────┐ │
│  │  ToolsController  [Authorize]  /api/tools                              │ │
│  │                                                                        │ │
│  │  GET    /                        → IToolCatalog.ListGranted            │ │
│  │  GET    /{name}                  → IToolCatalog.FindGranted (404 both  │ │
│  │                                    for absent AND ungranted)           │ │
│  │  POST   /{name}/invoke           → IDirectToolInvoker ← envelope bound │ │
│  │            [Authorize(Roles=Harness.Tools.Invoke)], off unless Enabled │ │
│  └───────────────────────────────┬────────────────────────────────────────┘ │
│  ┌───────────────────────────────┴────────────────────────────────────────┐ │
│  │  EvalsController  /api/evals    ← role-gated, off unless Enabled       │ │
│  │                                                                        │ │
│  │  GET    /datasets                → IEvalDatasetCatalog.ListNames       │ │
│  │  POST   /runs                    → StartEvalRunCommand ← envelope bound│ │
│  │  GET    /runs/{jobId}            → GetEvalRunQuery (status + report)   │ │
│  │  DELETE /runs/{jobId}            → CancelEvalRunCommand                │ │
│  │                                                                        │ │
│  │  Datasets are NAMED, never pathed — see "Evaluation" below             │ │
│  └───────────────────────────────┬────────────────────────────────────────┘ │
└──────────────────────────────────┼──────────────────────────────────────────┘
                                   │ MediatR (validation + audit behaviors)
                                   ▼
        Application.AI.Common/CQRS/Bundles/{RegisterBundle,RunBundle,GetBundleRun,DeleteBundle}
                                   │
                                   ▼
        Infrastructure.AI/Bundles/  BundleStagingService · BundleRunExecutor
                                    BundleRunBackgroundService · BundleWorkspaceCleanupService
                                    InMemoryBundleHandleStore · InMemoryBundleRunDispatchQueue
                                   │
                                   ▼
                       RunConversationCommand (the ordinary harness turn loop)
                       under an armed CapabilityEnvelope + EphemeralAgentOverlay
```

## Key Concepts

### The capability envelope is the load-bearing boundary

The host executes code it did not write, so a bundle's self-declared `allowed-tools`, MCP references, and autonomy are **requests**. The authoritative **grant** is the `CapabilityEnvelope` resolved by `ICapabilityEnvelopeResolver` from the caller's principal against `AppConfig:AI:BundleExecution:Envelopes`.

Two design points that must not be quietly broken:

- **The envelope is resolved in the controller, at the transport boundary**, from `User` -- not in the handler, and not from the handle. A run therefore executes under the grant of the credential that *invoked* it, so a leaked handle cannot escalate privilege.
- **It is fail-closed at every level.** An unmatched caller gets `Default`; an unconfigured `Default` grants nothing; an unparseable `AutonomyCeiling` degrades to `Restricted` (`ParseCeiling` deliberately rejects numeric and comma-composite values that `Enum.TryParse` would widen).

Resolution precedence is exact `sub`/name-identifier match → least-privilege combination of matching roles (intersection of allowlists, minimum ceiling) → `Default`.

> **Two identities, on purpose.** `BundleCallerIdentity.StableId` (ownership, rate-limit partitioning) prefers `oid`, then object-identifier, then name-identifier, then `sub`. `CapabilityEnvelopeResolver` (grant lookup) reads name-identifier or `sub` only. These differ for most Entra tokens. Ownership wants the most stable identifier; the grant table is keyed the way operators write config. Document this whenever you touch either -- it is the single most likely source of "my `BySubject` grant is ignored" reports.

### Ownership binding, non-disclosing

Every operation is owner-scoped. `RunBundleCommandHandler` and `DeleteBundleCommandHandler` check `IBundleHandleStore.GetOwner`; `GetBundleRunQueryHandler` matches `OwnerId` *and* `Handle` on the run record. A foreign resource is reported exactly as a missing one -- `404` for reads, a successful no-op `204` for delete -- so the API never confirms that someone else's handle exists. The stream endpoint reuses the poll query as its owner-scoped pre-flight before committing the response to an event stream.

### Staging: hostile input, guarded before it is parsed

`BundleStagingService` buffers the archive (bounded by `MaxArchiveBytes`), validates its shape from the central directory, extracts under guards, then -- and only then -- reuses the host's ordinary `AgentMetadataParser` / `SkillMetadataParser` / `IPluginManifestReader`. Guards: entry count, declared *and* actual uncompressed size, compression ratio (above a 1 MiB floor), zip-slip, escaping symlinks, and staging/discovery-root disjointness. Every non-success exit deletes the partial extraction; failure reasons describe the guard, never the payload.

The disjointness check exists because the global registries scan discovery roots recursively and are bundle-unaware: a staging root nested under one would let them publish a bundle's private skills globally.

### Two dispatch modes, one executor

`IBundleRunExecutor` is the single place the security ambients are armed. Both triggers call it, which is what stops them from diverging:

| Mode | `Stream` | Driver | Notes |
|------|----------|--------|-------|
| Background | `false` | `BundleRunBackgroundService` drains `IBundleRunDispatchQueue` | Serial -- one run at a time per host |
| Live SSE | `true` | The caller opening `GET .../stream`, via `BundleRunStreamer` | **Not enqueued.** Reclaimed on `StreamReservationTtl` if never claimed |

`BundleRunExecutor.ExecuteAsync` acquires the handle lease *before* claiming the run `Running` (so a run whose handle expired never carries a bogus start time), then claims via the atomic `IBundleRunJobStore.TryBeginRun` compare-and-set, so a stream racing the dispatcher for the same job yields exactly one winner. It arms `EphemeralAgentOverlayAccessor` (agent + owned skills resolve) and `CapabilityEnvelopeAccessor` (the governor enforces) around a materialised `RunConversationCommand`.

> The envelope rides on `BundleRunRecord` because the background dispatcher runs on a flow detached from the HTTP request -- the ambient published during the request does not reach it. **Without re-arming, the governor sees no envelope and fails open.** Any new run trigger must go through `IBundleRunExecutor`, never call `RunConversationCommand` directly.

### Streaming transport

`BundleRunStreamer` adds only the transport concern: it arms `AgentTurnStreamSink` so token deltas become `TEXT_MESSAGE_CONTENT` frames, and clears it in a `finally` so it can never leak onto a later request on the same thread. `BundleStreamEventWriter` serializes against the `BundleStreamEvent` *base type* so `[JsonPolymorphic]` emits the `type` discriminator -- serializing by runtime type silently drops it. The six event records deliberately duplicate a small subset of AG-UI rather than referencing the dashboard's 25-event vocabulary; if a third host needs them, extract a shared SSE primitive then.

### Fail-closed authentication, own audience

`AddExecutionApiAuthentication` throws at startup on three states: unconfigured without opt-in, half-configured (exactly one of `TenantId`/`ClientId`), and contradictory (`AllowAnonymous` *and* a configured scheme). Configured mode validates issuer, audience (`api://{ClientId}`), lifetime, and signing key with `ClockSkew = TimeSpan.Zero`, plus a fallback authorization policy so an endpoint added without explicit metadata still requires authentication.

Anonymous mode registers `AnonymousAuthenticationHandler` (one synthetic principal, so `[Authorize]` is satisfied) plus `ExecutionApiAnonymousModeStartupWarning`, which logs loudly for the life of the process. That principal carries no subject, so every anonymous run resolves to the fail-closed `Default` envelope -- the door is open, the room is empty.

### Rate limiting: two rate policies and one concurrency policy

Partitioned per caller by stable id, falling back to remote IP (the only distinguishing signal in anonymous mode).

| Policy | Kind | Limit |
|--------|------|-------|
| `bundles` | fixed window | 60 / minute — declared at controller scope, so it governs run, poll, and delete |
| `bundles-register` | fixed window | 10 / minute (staging is expensive) |
| `bundles-stream` | **concurrency** | `MaxConcurrentStreamsPerCaller` (default 4), `QueueLimit = 0` |

The stream policy is concurrency rather than rate on purpose: a streamed run executes inline on its connection for the whole conversation, so a request-rate limiter that counts *starts* cannot bound it.

## Project Structure

```
Presentation.ExecutionApi/
├── Program.cs                       Composition root + middleware pipeline (order is not negotiable)
├── Controllers/
│   ├── BundlesController.cs         The five endpoints; resolves the envelope; maps Result → ProblemDetails
│   ├── WorkflowsController.cs       Workflow submission, runs, progress stream, cancel
│   ├── ToolsController.cs           Read-only tool discovery, filtered by the caller's envelope
│   └── EvalsController.cs            Dataset listing + eval runs; names on the wire, never paths
├── DTOs/
│   ├── BundleApiContracts.cs        Register/Start/Run responses; BundleRunResponse projects the record
│   ├── WorkflowRunContracts.cs      Run start/cancel/status projections
│   ├── ToolCatalogContracts.cs      Catalog entry + listing; RiskTier travels as a name, not an ordinal
│   └── EvalRunContracts.cs          Eval request/response; report projects counts + cost, never transcripts
├── Extensions/
│   ├── ExecutionApiServiceCollectionExtensions.cs   Controllers, auth, FormOptions cap, rate limiters
│   └── ExecutionApiEvaluationExtensions.cs          Opt-in eval framework + RunKind.Evaluation executor;
│                                                    throws if enabled without roots
├── Services/
│   ├── BundleCallerIdentity.cs      Stable per-caller id (oid → objectidentifier → nameid → sub)
│   ├── AnonymousAuthenticationHandler.cs
│   └── ExecutionApiAnonymousModeStartupWarning.cs
├── Streaming/
│   ├── BundleRunStreamer.cs         Arms the text sink, calls IBundleRunExecutor, emits lifecycle frames
│   ├── BundleStreamEventWriter.cs   `data: {json}\n\n`, flushed, write-serialized
│   └── BundleStreamEvents.cs        The six AG-UI-shaped event records
├── appsettings.json                 Enables BundleExecution; Auth intentionally empty
└── appsettings.Development.json     Local opt-in (anonymous auth)
```

`BundleRunResponse.FromRecord` is the projection boundary: it drops the capability envelope, the seed messages, and every other execution input. **Do not widen it** -- the poll surface must never echo a run's security context back to the caller.

## Configuration

Everything lives under `AppConfig:AI:BundleExecution` (`Domain.Common/Config/AI/BundleExecution/`), validated by `Application.Core/Validation/BundleExecutionConfigValidator.cs`.

| Key | Default | Notes |
|-----|---------|-------|
| `Enabled` | `false` | Off ⇒ all four handlers return `Result.Forbidden` (`403`) |
| `TempRoot` | `""` → `%TEMP%/agent-bundles` | Must be disjoint from all skill/agent discovery roots |
| `MaxArchiveBytes` | 10 MiB | Also sets `FormOptions.MultipartBodyLengthLimit`, so oversize is rejected before MVC buffers the body |
| `MaxEntryCount` | 2000 | Read from the central directory, before a single byte is extracted |
| `MaxTotalUncompressedBytes` | 50 MiB | Checked against declared *and* actual bytes |
| `MaxCompressionRatio` | 100 | Only above a 1 MiB uncompressed floor |
| `HandleTtl` | 30 min | Sliding |
| `RunRecordTtl` | 30 min | Terminal records only |
| `StreamReservationTtl` | 5 min | Unclaimed streaming reservations; deliberately independent of `RunRecordTtl` |
| `MaxConcurrentStreamsPerCaller` | 4 | A concurrency limiter with `QueueLimit = 0` -- excess connections are rejected, not parked |
| `CleanupInterval` | 60 s | `BundleWorkspaceCleanupService` period; a lease held by a running job blocks its directory's deletion |
| `Envelopes` | fail-closed | `Default` / `BySubject` / `ByRole` |
| `Auth:TenantId`, `Auth:ClientId` | unset | This host's **own** audience -- never shared with AgentHub or the MCP server |
| `Auth:AllowAnonymous` | `false` | Explicit local-dev opt-in; `Environment=Development` alone does not disable auth |

The shipped `appsettings.json` sets `Enabled: true` with an empty `Auth` block, so it boots only in Development — where the shipped `appsettings.Development.json` opts into anonymous auth. Every other environment fails closed at startup until a real scheme is configured.

### Durable conversations (`AppConfig:AI:Conversations`)

A run may carry a `conversationId`, which makes it *continue* a stored conversation rather than run
one-shot: prior turns are replayed to the agent and this run's turns are appended (#235). The
transcript store, its turn lease and its token budget are shared infrastructure registered by
`Infrastructure.AI`, not by this host -- see `Domain.Common/Config/AI/Conversations/`.

| Key | Default | Notes |
|-----|---------|-------|
| `Provider` | `Sqlite` | `Sqlite` or `FileSystem`. Store, turn lease and budget switch together -- they are one choice, never mix them |
| `DatabasePath` | `data/conversations.db` | **Relative resolves against each host's own output directory.** Sharing conversations between this host and AgentHub therefore takes a deliberate *absolute* path in both |
| `MaxHistoryMessages` | 50 | How many recent messages are replayed to the model. Bounds prompt growth; the transcript itself is unbounded |

Two caps that now mean something narrower than they read: `userMessages` (100) and `maxTurns` (100)
bound **one run**. A durable conversation outlives any run, so what bounds its total length is the
conversation-lifetime token budget, which is durable and spans every run against that conversation.

That budget is `AppConfig:AI:AgentFramework:ConversationTokenBudget`, **on by default** at 1,000,000
cumulative tokens. An exhausted conversation declines further turns gracefully with an explanatory
message -- no model call, no error, and never mid-answer. Setting it to `0` disables it, at which point
**nothing** bounds a conversation's total length, because the per-run caps above deliberately do not.
See the XML docs on `AgentFrameworkConfig.ConversationTokenBudget` for the rationale and for how the
same ceiling applies to plan runs.

**Upgrading:** this shipped at `0` (disabled) before. A deployment whose own `appsettings.json` pins
`ConversationTokenBudget` to `0` keeps that value and stays unbounded -- the new default only reaches
hosts that never set it. Remove the pinned `0` to pick up the bound.

A conversation is created on first use and owned by the caller that first used it. A run naming
someone else's conversation is refused **synchronously** as `404` -- identically to an unknown handle,
so the endpoint cannot be used to enumerate other callers' conversation ids. Ownership itself is
enforced inside `IConversationStore`, never re-derived here.

### Direct tool invocation (`AppConfig:AI:DirectToolInvocation`)

Separate section, separate opt-in, and **the one surface in this host that ships off**. Bundle
execution and workflow submission set `Enabled: true` here because serving them is the host's
purpose; this one does not, and the asymmetry is the point. Those surfaces run an *agent* that
decides for itself which tools to use. This one lets a caller name the tool and the operation
directly, which is a materially larger grant — it should be the result of an operator deciding to
hand it out, not of adopting a template version in which it happened to be on.

| Key | Default | Notes |
|-----|---------|-------|
| `Enabled` | `false` | Off ⇒ `IDirectToolInvoker` refuses (`403`). The gate is in the invoker, not just the controller, so a future in-process caller cannot route around it |
| `MaxRequestBytes` | 64 KiB | Applied by `ToolInvocationRequestSizeLimitFilter` (a resource filter, so it runs before model binding — the only point at which the limit can still bite) |
| `InvocationTimeout` | 30 s | Server ceiling over the **whole invocation** — authorization and the classification gate run under it too, not just the tool. A caller may request **less**; requesting more is refused, never clamped |
| `MaxOutputCharacters` | 256 Ki | Output beyond this is truncated and the response says so (`outputTruncated`) |
| `MaxParameterCount` | 64 | Bounds the request's *shape*, which the byte ceiling does not |

**Bounded by concurrency, not request rate.** The invoke action carries its own
`InvokeRateLimitPolicy` — a per-caller `ConcurrencyLimiter` of 4, `QueueLimit = 0` — replacing the
controller's 60/min fixed window for that action only. Same reasoning as `StreamRateLimitPolicy` one
function above it: an invocation runs inline on its request thread for up to `InvocationTimeout` and
does not go through the background dispatcher, so a limiter that counts *starts* cannot bound work
that is still executing when the window rolls.

All four settings are validated at startup by `DirectToolInvocationConfigValidator` with `ValidateOnStart`.
Two of them fail in ways a caller cannot diagnose, which is why doc-only "must be positive" was not
enough: a non-positive `MaxOutputCharacters` slices the output with a negative length, so a
*successful* tool call surfaces as a `500`; a non-positive `InvocationTimeout` cancels every
invocation before the tool starts, so the surface answers `504` to everything. Both read as a broken
host rather than a mistyped limit, so the host refuses to start and names the setting instead.

Three gates apply to every invocation, and none substitutes for another: the caller must
authenticate; must hold the `Harness.Tools.Invoke` role; and the tool must be in the capability
envelope their credential resolves to. Arming that envelope is what switches
`ToolInvocationGovernor` enforcement on, so the third gate is enforced twice — once by the catalog
lookup and again, independently, by the governor.

Note the ceiling interaction inherited from the envelope: only an `Autonomous` `AutonomyCeiling`
currently permits tool execution at all. A caller whose envelope grants `file_system` under a
`Supervised` ceiling is refused with `403`, not `404` — they are granted the tool, and governance
declined this invocation of it.

### Evaluation (`AppConfig:AI:Evaluation`)

Separate section, separate opt-in. The evaluation framework is **not** wired by `GetServices()` — a
host that never evaluates should not carry the YAML loader, the metric singletons, the reporters, and
the harness agent invoker on every cold start. `AddExecutionApiEvaluation` adds them, and runs
*after* `AddExecutionApiServices` so the real `IEvalRunner` replaces the fail-fast
`NotConfiguredEvalRunner` by last-write-wins.

| Key | Default | Notes |
|-----|---------|-------|
| `Enabled` | `false` | Off ⇒ framework unregistered; any dispatch hits `NotConfiguredEvalRunner` and throws loudly rather than appearing to work |
| `DatasetRoots` | `[]` | Directories dataset files may be read from. **Empty means unconfined.** Use absolute paths — a relative entry resolves against the working directory, not the content root. Blank entries are dropped |
| `MaxDatasetBytes` | 5 MiB | Checked before the file is opened, so an oversized dataset is never parsed |
| `MaxDatasetsPerRun` | 10 | Enforced by the validator |
| `MaxCaseExecutionsPerRun` | 500 | Cases **× `Repeats`** — what the run actually costs. Counted before tag filtering (over-estimates, so it errs toward refusing); enforced by the handler |
| `MaxRepeats` | 50 | Was the validator's hard-coded bound |
| `MaxParallelism` | 128 | Previously enforced **only** by the EvalRunner CLI's argument parsing |

> **`Enabled: true` with no usable `DatasetRoots` throws at startup.** This is deliberate, and it is
> the whole reason the section exists. Evaluation reads dataset files named by the caller; with no
> roots configured that is unconfined — correct for the CLI on a developer's own machine, and an
> arbitrary-file-read probe on a host anyone else can reach. A host that booted anyway would *look*
> configured while honouring whatever path it was sent. Whitespace-only entries do not count: a gate
> you can tick with a space is not a gate.

Confinement itself lives in `EvalDatasetPathGuard`, applied by `RunEvalSuiteCommandHandler` rather
than by each caller — a check every dispatcher must remember to perform is one that will eventually
be skipped. It canonicalises before comparing (so `..` cannot walk out), resolves symlinks to their
final target (a link inside a root can point anywhere), and compares against the root **plus a
separator** (without it, `/data/evals-secret` reads as inside `/data/evals`). Refusals do not
distinguish *forbidden* from *absent*, or the endpoint becomes a filesystem oracle.

**Confinement ratchets.** Adding a root at runtime tightens immediately; emptying the list refuses
everything rather than returning to unconfined. That verdict is latched at composition time by
`AddExecutionApiEvaluation` (as an `EvalConfinementLatch`) rather than derived inside the guard,
because the guard is a lazy singleton — a latch it computed for itself would first run on the initial
eval dispatch and record whatever a `reloadOnChange` had done to the config by then.

> **Deployment invariant — a configured root must not be writable by anyone you would not already let
> read arbitrary files.** This one cannot be enforced in code. Anyone who can write inside a root can
> place a *hard* link there (no target to resolve, indistinguishable from an ordinary file), or swap a
> file for a symlink between the check and the open. Confinement bounds which **paths** a caller may
> name; it cannot bound what a writable directory turns out to contain.

#### The HTTP surface: names, not paths

`RunEvalSuiteCommand` takes dataset **paths**, which is right for the CLI — a developer pointing the
runner at a file on their own machine. `/api/evals` never exposes that shape. A caller names a
dataset; `IEvalDatasetCatalog` maps the name to a file by **enumerating** the configured roots and
matching, never by concatenating the name onto a root. The distinction is the whole design:

- **There is no path field on the wire.** A name cannot express "outside the roots", so the dangerous
  request is *unrepresentable* rather than rejected. The guard still runs underneath, so a future
  caller that reintroduced a path would still be confined — the wire shape makes the attack unsayable,
  the guard makes it ineffective.
- **A dataset is whatever an operator put at the top level of a root.** Not recursive: a name carrying
  structure would be a path with extra steps, needing to be split, rejoined and validated. Operators
  organise by root, not by folder.
- **No roots ⇒ no catalog.** `GET /datasets` answers empty rather than enumerating the working
  directory. Listing is a disclosure, and there is nothing bounded to disclose until an operator says
  what the bounds are.
- **`Resolve` cannot distinguish "unknown" from "malformed".** Both answer nothing, for the same
  reason the guard's refusals do not distinguish forbidden from absent.

**This surface is role-gated, unlike every other route this host serves.** Bundles, workflows and
tool discovery all run the *caller's own* work under the caller's own grant; this one spends the
host's model budget on the operator's suites. That is a different kind of authority from holding a
valid token, so it carries `Harness.Evals.Execute`, following the same `Harness.<area>.<verb>`
convention as `Harness.Drift.Operate`. One role covers the whole surface rather than a read/execute
split: the read endpoints only ever answer about the caller's *own* runs, so a reader who cannot
start one has nothing to read. The anonymous development principal is granted the role so a local
developer can exercise the surface at all -- that mode is an explicit opt-in that already boots with
a warning.

The role decides *whether you may evaluate*; ownership still decides *which runs are yours*. Holding
it does not let a caller read another's run.

Runs execute on the **shared run substrate** (`RunKind.Evaluation`), not inline: a suite is hundreds
of governed agent turns at the default ceilings, so `POST /runs` answers 202 with a job id. Three
properties are worth knowing:

| Property | Why |
|----------|-----|
| The caller's `CapabilityEnvelope` is armed around the evaluation | Every case is a governed agent turn that can invoke tools. Without it, a caller reaches tools it is denied directly by putting them in an eval case |
| `TargetId` is the run's own job id | Admission refuses a second live run per target — right for a workflow's shared plan state, wrong for evaluations, which share nothing. A per-run target makes the check correctly inert here instead of switching it off where it also governs workflows |
| The per-owner ceiling is `AI:WorkflowSubmission:MaxConcurrentRunsPerOwner` | The store's counter is cross-kind, and the substrate's other knobs (`RunRecordTtl`, sweep interval) already live in that section. The name reads narrower than what it governs; a second ceiling on the same counter would mean the limit binding a caller depended on which endpoint they last called |

**Cancelling is weaker than on the workflow path, deliberately.** A queued run is cancelled exactly; a
run already executing answers `200` with `stopped: false`. A workflow in flight can be signalled
through `IPlanRunCancellationRegistry`; an evaluation is a suite of agent turns with no equivalent, so
the honest answer is to report that rather than claim an interruption that will not happen. What
bounds a runaway suite is `MaxCaseExecutionsPerRun`, applied *before* any case runs.

**A finished run's report is filed in-process.** The executor dispatches `IngestEvalRunCommand`
itself, which is what a server-side run buys over the CLI -- the CLI produces a report and posts it
back over HTTP to be ingested, and a run already executing inside the host has no reason to leave the
process to file its own result. A failed ingest is logged and never fails the run: the evaluation
ran, the spend is incurred, and marking it failed would invite a retry that pays for the whole suite
again to fix a bookkeeping problem.

**A run's report lives beside its record, not on it.** `RunRecord` is deliberately kind-agnostic, so
`IEvalRunSubmissionStore` holds the dataset names and the report, keyed by the same job id and
reclaimed by the same sweep — through `IRunReclaimListener`, so the sweeper does not grow a dependency
per run kind. `GET /runs/{jobId}` returns counts, verdict, duration and cost, **not** per-case results:
those hold every case's input and the agent's full output, which is not something a status poll should
carry.

## How to Run

```bash
# Configure the audience (or rely on the development anonymous opt-in)
dotnet user-secrets --project src/Content/Presentation/Presentation.ExecutionApi \
  set "AppConfig:AI:BundleExecution:Auth:TenantId" "<tenant-guid>"
dotnet user-secrets --project src/Content/Presentation/Presentation.ExecutionApi \
  set "AppConfig:AI:BundleExecution:Auth:ClientId" "<client-guid>"

dotnet run --project src/Content/Presentation/Presentation.ExecutionApi
```

There is no `Properties/launchSettings.json`, so set `ASPNETCORE_URLS` explicitly rather than assuming a port. Outside Development the pipeline adds HSTS and HTTPS redirection. `UseDefaultServiceProvider(ApplyValidationPolicy)` enforces `ValidateScopes` + `ValidateOnBuild` in **all** environments, so a mis-wired handler fails at startup rather than on first dispatch.

## Common Tasks

### Grant a caller some capabilities

Add an entry under `Envelopes:BySubject` keyed on the caller's `sub`/name-identifier claim (**not** `oid`), or under `Envelopes:ByRole` keyed on an app role. Remember that multiple matching roles *intersect*, and that only an `Autonomous` ceiling currently permits tool execution -- `Supervised`/`Restricted` suspend tool use entirely because mid-run approval routing is deferred (documented on `CapabilityEnvelope.AutonomyCeiling`).

### Decide what to put in a caller's `AllowedTools`

`GET /api/tools` answers this from the caller's side, but two things are the operator's job and no
endpoint can decide them for you.

**Never grant these over HTTP.** `dashboard_control` and the `render_*` tools (`render_chart`,
`render_image`, `render_form`, `render_table`) exist to drive an interactive client through
`IClientToolBridge`, which **only AgentHub registers**. Granting them to an HTTP caller is
meaningless at best: there is no client on the other end to render into. `echo_lookup` and
`echo_calculate` are demo fixtures — grant them in a smoke test, never in an environment that
matters.

Those same tools — plus `delegate_task` — additionally declare `IsDirectlyInvocable = false`, so
`POST /{name}/invoke` answers `404` for them even when an envelope grants them. That flag is a
*transport-suitability* declaration, not an authorization control: it says the tool's result means
nothing outside the process (a render directive for a browser that is not there) or that one call
expands into unbounded work (`delegate_task` runs whole agent turns behind what looks like a single
synchronous call). They remain fully usable from a workflow's `ToolUse` step, which is why the
catalog still lists them with `isDirectlyInvocable: false` rather than hiding them.

**Registered is not the same as available.** Those same five tools are registered in *every* host
because tool registration is shared, but in any host without `IClientToolBridge` they cannot be
constructed at all. The catalog resolves tools one key at a time precisely so one such tool cannot
fail the whole listing; it omits them and logs a warning naming the key. If you see that warning,
it is telling you the truth about the host — not about the catalog. (The underlying sloppiness —
hosts registering tools whose dependencies they do not provide — predates the catalog and is
tracked separately; the catalog only made it visible.)

### Add an endpoint

Put the behavior in a MediatR command/query under `Application.AI.Common/CQRS/Bundles/` with a FluentValidation validator, then add a thin action to `BundlesController`: resolve `ResolveCallerId()`, pass `OwnerId`, and map failures through the existing `MapFailure`. Do not surface raw error text on the general-failure path -- handlers log the detail; the wire gets a generic message.

### Make runs durable

Replace `InMemoryBundleHandleStore`, `InMemoryBundleRunJobStore`, and `InMemoryBundleRunDispatchQueue` behind their existing interfaces (`Application.AI.Common/Interfaces/Bundles/`). Preserve two contracts: `TryBeginRun` must stay an atomic compare-and-set (it is what makes claiming exactly-once), and a handle lease must continue to pin the staging directory against the cleanup sweeper for the life of a run.

### Change the wire contract

**This is the canonical list.** Nothing is generated from the controllers, so nothing mechanically catches drift -- every one of these describes the contract by hand and must be updated in the same commit:

1. `documentation/onboarding/assets/openapi/bundle-api.yaml` -- the OpenAPI spec (routes, schemas, status codes, examples). **The filename is deliberately stale**: it is a published URL that external consumers and chapter 17 both link to, so it outlived the "Bundle API" name. `info.title` is authoritative.
2. `documentation/onboarding/17-bundle-api.html` -- the consumer guide (endpoint table, error table, config table, SSE frame table, quickstart).
3. This README -- the route block in *Architecture Context* and the *Configuration* table above.
4. `CLAUDE.md` -- only if the spec's location or the doc-site page count changes.

The spec covers **all three route families this host serves** -- bundles, workflows, and tool
discovery -- not just bundles. That was not true until the tool catalog shipped: the workflow routes
(W3--W6) went in over four PRs without a single spec entry, and the drift was only caught when
someone went looking. If you add a route here, add it to the spec in the same change. A published
contract that describes two thirds of the surface is worse than one that admits its gaps, because
consumers cannot tell which third is missing.

`BundleRunStatus` numeric values are anchored by a `Domain.AI` enum test -- add new states at the end only.

## Dependencies

| Reference | Why |
|-----------|-----|
| `Presentation.Common` | `GetServices()` composition + `ApplyValidationPolicy` + security-headers/exception middleware |
| `Application.AI.Common` | The bundle CQRS contracts and `ICapabilityEnvelopeResolver` |
| `Domain.AI` | `BundleRunRecord`, `BundleRunStatus`, `CapabilityEnvelope` |
| `Domain.Common` | `BundleExecutionConfig`, `BundleApiAuthConfig` |
| `Microsoft.AspNetCore.Authentication.JwtBearer` | The Entra scheme |

`InternalsVisibleTo` is granted to `Presentation.ExecutionApi.Tests`.

## Testing

`src/Content/Tests/Presentation.ExecutionApi.Tests/` covers the host end to end:

| File | Covers |
|------|--------|
| `BundlesControllerIntegrationTests.cs` | Full-stack HTTP via `WebApplicationFactory<Program>` -- boot (proves `ValidateOnBuild` passes), 404/400/204 paths, staging-guard rejection |
| `BundleStreamingIntegrationTests.cs` | The SSE endpoint over the real pipeline |
| `BundleRunStreamerTests.cs` | Frame sequencing, terminal-frame exclusivity, sink cleanup |
| `ExecutionApiAuthenticationTests.cs` | The three fail-closed startup guards |
| `BundleCallerIdentityTests.cs` | Claim-precedence for the stable id |
| `ToolsControllerIntegrationTests.cs` | Envelope-filtered discovery, 404 for ungranted, 401 against a configured host, and that every catalogued name resolves to a tool agreeing with it |
| `ExecutionApiEvaluationEnablementTests.cs` | The evaluation opt-in: default leaves the fail-fast runner, enabled-without-roots refuses to boot, blank roots do not satisfy it |
| `EvalRunsIntegrationTests.cs` | That `/api/evals` is mounted, closed to anonymous callers on every route, and serves nothing while evaluation is disabled -- the default this host ships with |
| `ToolInvocationIntegrationTests.cs` | That `POST /api/tools/{name}/invoke` is mounted, refuses anonymous callers and authenticated callers lacking `Harness.Tools.Invoke`, runs nothing while direct invocation is disabled, and that listing does **not** require the invoke role -- the separation is the point |

Direct invocation's behaviour once *enabled* -- envelope arming, governance, the classification gate,
deadlines, sanitization, truncation -- is covered in
`Application.AI.Common.Tests/Services/Tools/DirectToolInvokerTests.cs`, driven through a real container
so the DI scope, the scoped execution context and keyed tool resolution are the production ones. Every
refusal there asserts against the fake tool's own execution counter rather than only the returned
status: a test checking the status alone would pass against an invoker that ran the tool and *then*
decided to refuse, which is the failure actually worth catching.

That file also carries a note about the one property deliberately **not** tested -- that the ambient
accessors are clear on return. Both are `AsyncLocal<T>`, so a value set inside an awaited async method
is invisible to the caller whatever the callee did, and the assertion was demonstrated (by mutation, and
by a standalone probe) to pass against an invoker that cleaned up nothing. It was removed rather than
kept as false assurance. The identical assertion was removed from `EvalRunKindExecutorTests` for the
same reason.

Evaluation's behaviour once *enabled* -- admission, ownership, reports, cancellation -- is covered in
`Infrastructure.AI.Evaluation.Tests/Runs/EvalRunHandlerTests.cs` against the real run and submission
stores, which can drive it without standing up an agent and a model provider. `EvalDatasetCatalogTests`
covers name resolution against real directories, deliberately: the property under test is that
resolution happens by *enumerating what is there*, and a faked filesystem would pass just as happily
against an implementation that concatenated the name onto a root.

```bash
dotnet test src/Content/Tests/Presentation.ExecutionApi.Tests
```
