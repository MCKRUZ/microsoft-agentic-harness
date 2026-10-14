# Agent Lightning: fit assessment for the harness

Status: assessment for [#832](https://github.com/MCKRUZ/microsoft-agentic-harness/issues/832). Written 14 Oct 2026 against Agent Lightning v1.0.2 (released 29 Sep 2026).

## Verdict

**Conditional go, for an optional integration, not a default.** The wire path is viable with no change to the harness's chat-client layer. What does not exist yet is a rollout executor, a reward reporter and a governance stance for training runs. Three conditions apply:

1. Train only on curated or synthetic datasets, never on production conversations (see [Data](#3-data-handling-what-is-trained-on)).
2. Use a provider that speaks Chat Completions. The Foundry Responses providers cannot take part.
3. Treat it as value for teams running an open-weight model. It does nothing for hosted models.

## How each claim was checked

| Tag | Meaning |
|---|---|
| **Verified** | Read in the vendor's source or documentation, or in this repository's code, for this assessment. |
| **Inferred** | Follows from verified facts but was not run. |
| **Unverified** | Not checked. Listed again at the end. |

Vendor sources: the README, `docs/05-basics.md`, `docs/25-api-gateway-configuration.md`, `docs/30-controller-configuration.md`, `docs/20-trainer-configuration.md`, and `agentlightning/server/routes/proxy.py` and `events.py` on `main`.

## What Agent Lightning does

Verified. A Python reinforcement-learning trainer for agents. Three parts:

- **API Gateway:** an OpenAI-compatible reverse proxy. The agent sends its model requests to a *rollout-specific* URL, and the gateway forwards them to the model being trained and records prompt and response token IDs and log-probabilities.
- **Rollout Controller:** starts one agent execution per rollout, either as a local Python subprocess or as a Kubernetes Job from a Jinja template. It injects `AGL_OPENAI_BASE_URL`, `AGL_EVENT_URL` and `AGL_KEY` into each execution.
- **Trainer:** runs `verl` and vLLM on the GPU, turns the captured calls and rewards into training samples, and updates the policy.

The agent keeps its own tools, context and control flow. Training changes the model weights, not the agent's prompts.

## Findings against the six questions in #832

### 1. Wire compatibility

- **Verified:** the gateway accepts only `chat/completions` and `completions`. Any other path returns 404 `Unsupported upstream path` (`routes/proxy.py`, line 44). **The Responses API is not supported.**
- **Verified:** in this repository the `OpenAI` and `AzureOpenAI` providers use the OpenAI SDK `GetChatClient(...).AsIChatClient()`, which is Chat Completions (`ChatClientFactory.Providers.cs`). `FoundryResponses` and `FoundryDirectResponses` use the Responses API and cannot be trained through the gateway.
- **Verified:** `ClientType=OpenAI` with `AppConfig:AI:AgentFramework:Endpoint` set already targets "an OpenAI-compatible gateway" (`DependencyInjection.Tools.cs`, the `OpenAI` case; `AgentFrameworkHelper.GetOpenAIClientOptions`). No factory change is needed.
- **Inferred:** the per-rollout URL arrives as an environment variable, so a Job template can map `AGL_OPENAI_BASE_URL` to `AppConfig__AI__AgentFramework__Endpoint` and `AGL_KEY` to the API key through standard .NET environment-variable configuration.
- **Unverified:** streaming and tool-calling behaviour through the gateway; the `AzureOpenAI` type additionally requires HTTPS unless the host is loopback (`AzureOpenAIV1Endpoint`), so use the `OpenAI` type for an in-cluster HTTP gateway.

### 2. Governance

- **Inferred, high confidence:** content safety, token budgets and the tool-call chokepoint wrap the `IChatClient` inside `AgentFactory`, above the endpoint, so a different base URL does not remove them.
- **Verified risks that are new:**
  - The gateway holds raw prompts, completions and token IDs in its own store (an in-process dictionary in `events.py`), outside the harness's audit and redaction path.
  - An empty gateway `key` disables authentication and only logs a warning.
  - The proxy **forces** the sampling temperature (train 1.0 by default). It overrides harness settings, including the `--deterministic` flag.
  - Rollouts execute real tools. They need the sandbox's closed-by-default capability model and no production credentials.
- **Unverified:** whether the gateway store persists beyond process lifetime.

### 3. Data handling: what is trained on

The risk is not redaction in transit; it is **dataset provenance**. The harness enforces tenant and owner isolation for knowledge. Weights have no such boundary: training on conversations from several tenants would blend them into one model. Condition 1 above follows from this. Trained weights should also be treated as a data-classified artifact.

### 4. Hardware

- **Verified:** this machine has an NVIDIA RTX 5090 (32 GB), Docker, `kubectl` and WSL2. The NAS has no GPU.
- **Verified:** the vendor's local runner needs POSIX process groups and does not run on native Windows. Use the Kubernetes runner (Docker Desktop) or WSL.
- **Unverified:** whether 32 GB suffices for a given model size. The vendor documents its smallest example as needing one GPU.

### 5. Reward source

- **Verified:** reward is posted over plain HTTP, so it is language-neutral: `POST /rollouts/{rollout_id}/attempt/{attempt_id}/events` with `event_type: "reward"` and `data: {"value": <scalar>}` (`routes/events.py`).
- **Verified:** `Presentation.EvalRunner` already replays dataset cases through the real agent, supports repeats, and takes pluggable reporters (`IEvalReporter`). Each `EvalResult` carries per-metric `AggregatedScores` and a `Verdict`.
- **Gap:** a scalar reward needs a defined formula, and the judge panel is an LLM and can be gamed by the policy being trained. Prefer deterministic metrics (exact match, tests) for the first run. The vendor's coding example ships a reward-hacking prevention step.

### 6. Maturity

Verified: v1.0.2; the README states v1.0 is "completely refactored" and earlier releases live on a separate branch. MIT licence. Expect API churn, so pin the version.

## What would have to be built

| Piece | Status |
|---|---|
| Chat-client wiring to the gateway | Exists (`OpenAI` type plus `Endpoint`). Configuration only. |
| Rollout executor | `EvalRunner` is the natural base. It needs a one-case dataset built from the rollout input. |
| Reward reporter | New `IEvalReporter` that posts the scalar to `AGL_EVENT_URL`. |
| Kubernetes Job template | New. Maps the injected variables to harness configuration. |
| Governance stance for training runs | Needs a decision: sandboxed tools, no production secrets, authenticated gateway, curated data only. |

## Relationship to existing work

The Skill Training Loop (the SkillOpt port) edits a skill's *text*; Agent Lightning would tune the *model*. They are different mechanisms and should not share an abstraction. `IRolloutRunner` in skill training is a separate seam.

## Recommendation: a scoped spike

One harness agent, one dataset scored by a deterministic metric, the local Docker Desktop Kubernetes cluster and the RTX 5090. Success means all of:

1. The reward rises over a fixed number of training steps.
2. Content safety and the tool-call chokepoint still fire on every rollout.
3. No production data or credentials are reachable from a rollout.

If the Responses API is required for the target agents, or the governance controls cannot be kept, record a no-go instead.

## Unverified, to settle in the spike

- Streaming and tool-calling through the gateway.
- Whether the gateway store persists, and where.
- Whether 32 GB fits the chosen model.
- The exact `EventCreate` body beyond `event_type` and `data`.
- The environment-variable mapping (inferred, not run).

## Sources

- https://github.com/microsoft/agent-lightning (README, v1.0.2)
- https://microsoft.github.io/agent-lightning/stable/
- https://arxiv.org/abs/2608.17528 (technical report)
