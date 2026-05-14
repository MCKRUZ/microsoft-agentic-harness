# Phase 2: Enterprise Trust — Implementation Plan

## Context

This plan implements two platform gaps identified by comparing the Microsoft Agentic Harness against enterprise agentic platform architecture: **Human Escalation** and **Fallback Chains**. Phase 1 (committed at `8edf626`) delivered autonomy tiers and a supervisor agent. Phase 2 builds on those foundations to add trust governance: agents that exceed their authority trigger structured approval workflows, and LLM provider failures degrade gracefully through resilient fallback chains.

The harness is a production-grade C# .NET 10 template built on Clean Architecture (Domain → Application → Infrastructure → Presentation), CQRS/MediatR, and the Microsoft Agent Framework. All new code follows existing conventions: immutable records, keyed DI, Options pattern config, OTel instrumentation, and xUnit + Moq testing.

### Relationship to Existing GovernanceApprovalWorkflow

The codebase already has a governance approval workflow at `Application.Core/Workflows/Governance/` using MAF's `RequestPort<ApprovalRequest, ApprovalResponse>` pattern. This workflow has `CreateApprovalRequestExecutor` and `ProcessApprovalOutcomeExecutor` stubs. Phase 2 **extends this existing workflow** rather than building a parallel approval system:

- `IEscalationService` is the orchestrator that constructs `EscalationRequest` records, manages the approval lifecycle, and ultimately feeds resolved outcomes back through the existing `GovernanceApprovalWorkflow`'s `RequestPort`
- The existing `ApprovalRequest`/`ApprovalResponse` types are kept as the MAF port message shapes
- New `EscalationRequest`/`EscalationOutcome` records are the richer domain models that carry strategy, timeout, and multi-approver semantics — they wrap the simpler MAF types
- `CreateApprovalRequestExecutor` is updated to delegate to `IEscalationService` rather than being reimplemented

### Known Limitation: In-Memory Escalation State

`DefaultEscalationService` stores active escalations in an in-memory `ConcurrentDictionary`. If the process restarts while an escalation is pending, the in-memory state is lost. The `JsonlEscalationAuditStore` records all events for compliance, but there is no automatic recovery path to reconstitute pending escalations from the audit log on startup. On startup, the service logs a warning if unresolved entries exist in the JSONL store. Durable state recovery is a Phase 3+ concern.

---

## Part A: Human Escalation

### A1. Domain Models — Escalation Primitives

**Layer:** `Domain.AI`

**Why:** The escalation subsystem needs its own value types that are independent of governance policy evaluation. An `EscalationRequest` is the structured representation of "this agent tried something beyond its authority." An `EscalationOutcome` represents the human's decision. These are domain concepts, not infrastructure.

**New types in `Domain.AI/Escalation/`:**

`EscalationRequest` record:
- `EscalationId` (Guid) — unique identifier
- `AgentId`, `ToolName`, `Arguments` (IReadOnlyDictionary) — what was attempted
- `Description` — human-readable summary of the action
- `RiskLevel` — derived from matched governance rule
- `Priority` — `Informational`, `Blocking`, `Critical` (enum `EscalationPriority`)
- `ApprovalStrategy` — `AnyOf`, `AllOf`, `Quorum` (enum `ApprovalStrategyType`)
- `Approvers` — ordered list of approver identifiers
- `QuorumThreshold` — for Quorum strategy, the N in N-of-M
- `TimeoutSeconds` — how long before expiry
- `TimeoutAction` — `Deny`, `DenyAndEscalate`, `Approve`, `Escalate` (enum `EscalationTimeoutAction`)
- `RequestedAt` — timestamp
- `OriginatingDecision` — the `GovernanceDecision` that triggered this (nullable, for correlation)

`EscalationOutcome` record:
- `EscalationId` — correlates back to request
- `IsApproved` — final verdict
- `Decisions` — `IReadOnlyList<ApproverDecision>` (who approved/denied, when, with reason)
- `ResolutionType` — `Approved`, `Denied`, `TimedOut`, `Escalated` (enum `EscalationResolutionType`)
- `ResolvedAt` — timestamp
- `EscalatedToTier` — if resolution was escalation, which tier received it (nullable)

`ApproverDecision` record:
- `ApproverName`, `Approved` (bool), `Reason` (nullable), `RespondedAt`

`EscalationAuditRecord` record:
- `RecordType` — `Request`, `Decision`, `Outcome` (enum `EscalationAuditRecordType`)
- `EscalationId` (Guid)
- `Timestamp` (DateTimeOffset)
- `Payload` — the serialized request, decision, or outcome (variant based on `RecordType`)

`EscalationPriority` enum: `Informational = 0`, `Blocking = 1`, `Critical = 2`

`EscalationWaitBehavior` enum: `Block`, `QueueAndContinue` — configured per autonomy tier to control whether the agent pauses or continues other work while waiting.

**Relationship to existing types:** `AutonomyExceededResult` (from Phase 1) provides the `AttemptedAction`, `CurrentLevel`, `RequiredLevel`, and `Reason` that feed into building an `EscalationRequest`. `GovernanceDecision` with `Action == RequireApproval` is the other trigger path. Both converge into the same escalation pipeline.

### A2. Approval Strategies

**Layer:** `Application.AI.Common` (interface), `Application.Core` (implementations)

**Why:** Different escalation scenarios need different approval logic. A routine read-access escalation needs one approver fast (AnyOf). A destructive action needs unanimous consent (AllOf). A balanced workflow needs majority agreement (Quorum). The strategy pattern makes this pluggable and configurable per policy rule.

**Interface in `Application.AI.Common/Interfaces/Escalation/`:**

`IApprovalStrategy`:
- `EvaluateDecision(EscalationRequest request, IReadOnlyList<ApproverDecision> decisions)` → returns `ApprovalEvaluation` (record with `IsResolved`, `IsApproved`, `PendingApprovers`)
- `StrategyType` property → `ApprovalStrategyType` enum value

**Implementations in `Application.Core/Escalation/Strategies/`:**

`AnyOfApprovalStrategy` — resolved as soon as any single approver responds. If approved, immediately unblock. If denied, immediately deny. First response wins.

`AllOfApprovalStrategy` — all designated approvers must respond with approval. A single denial immediately resolves as denied. Only resolves as approved when every approver has approved.

`QuorumApprovalStrategy` — resolved when enough approvers have responded to determine the outcome. If `approvedCount >= quorumThreshold`, approve. If `deniedCount > (totalApprovers - quorumThreshold)`, deny (impossible to reach quorum). Requires `QuorumThreshold` from the request.

**Registration:** Keyed DI by `ApprovalStrategyType`. Factory or dictionary lookup resolves the strategy from the request's `ApprovalStrategy` field.

### A3. Escalation Service

**Layer:** `Application.AI.Common` (interface), `Infrastructure.AI` (implementation)

**Why:** This is the orchestrator that ties together escalation creation, notification delivery, approval tracking, timeout management, and outcome resolution. It's the single entry point that the governance pipeline calls when escalation is needed.

**Interface in `Application.AI.Common/Interfaces/Escalation/`:**

`IEscalationService`:
- `RequestEscalationAsync(EscalationRequest request, CancellationToken ct)` → `Task<EscalationOutcome>` — for blocking behavior
- `QueueEscalationAsync(EscalationRequest request, CancellationToken ct)` → `Task<Guid>` — for queue-and-continue behavior (returns escalation ID for later polling)
- `SubmitDecisionAsync(Guid escalationId, ApproverDecision decision, CancellationToken ct)` → `Task<EscalationOutcome?>` — returns outcome if the decision resolves the escalation, null if still pending
- `GetPendingEscalationAsync(Guid escalationId, CancellationToken ct)` → `Task<EscalationRequest?>` — for UI polling
- `GetPendingEscalationsAsync(string approverName, CancellationToken ct)` → `Task<IReadOnlyList<EscalationRequest>>` — list pending for an approver

**Implementation in `Infrastructure.AI/Escalation/`:**

`DefaultEscalationService`:
- Maintains an in-memory `ConcurrentDictionary<Guid, EscalationState>` for active escalations
- `EscalationState` tracks the request, collected decisions, a `TaskCompletionSource<EscalationOutcome>` for blocking callers, and a `CancellationTokenSource` for timeout
- On request: resolve `IApprovalStrategy` from keyed DI, notify all approvers via `IEscalationNotifier`, start timeout timer
- On decision: evaluate via strategy, if resolved → complete the TCS, audit, notify resolution
- On timeout: execute `TimeoutAction` from request (default: deny + escalate to next tier)

**Timeout implementation:** `Task.Delay(timeout, cts.Token)` races against the TCS. If delay wins, the timeout action fires. If TCS completes first, the delay is cancelled. No background timer service needed — the timeout is scoped to the escalation lifetime.

**Cancellation propagation:** The caller's `CancellationToken` is linked to the escalation's `CancellationTokenSource` via `CancellationTokenSource.CreateLinkedTokenSource()`. If the caller disconnects or the HTTP request is cancelled, the escalation timeout also cancels and the TCS completes with `OperationCanceledException`. This prevents zombie escalations running after the caller is gone.

### A4. Notification Adapters

**Layer:** `Application.AI.Common` (interface), `Infrastructure.AI` + `Presentation.AgentHub` (implementations)

**Why:** Escalation notifications must reach humans through whatever channel they're monitoring. The dashboard gets AG-UI events. External systems (Slack, Teams) get notifications through adapters. The composite pattern fans out to all registered notifiers so adding a channel is just DI registration.

**Interfaces in `Application.AI.Common/Interfaces/Escalation/`:**

`IEscalationNotifier` — the public-facing contract consumed by `IEscalationService`:
- `NotifyEscalationRequestedAsync(EscalationRequest request, CancellationToken ct)`
- `NotifyEscalationResolvedAsync(EscalationOutcome outcome, CancellationToken ct)`
- `NotifyEscalationExpiringAsync(Guid escalationId, TimeSpan remaining, CancellationToken ct)`

`IEscalationNotificationChannel` — the inner contract implemented by individual adapters (same method signatures as `IEscalationNotifier`). This separation avoids infinite recursion: `CompositeEscalationNotifier` implements `IEscalationNotifier` and injects `IEnumerable<IEscalationNotificationChannel>` — it never receives itself.

**Implementations:**

`CompositeEscalationNotifier : IEscalationNotifier` (Infrastructure.AI) — injects `IEnumerable<IEscalationNotificationChannel>`, fans out calls to all. Catches and logs individual channel failures without blocking others.

`AgUiEscalationNotifier : IEscalationNotificationChannel` (Presentation.AgentHub) — creates AG-UI events and pushes them through the existing SSE stream. New event types added to `AgUiEventType`: `ESCALATION_REQUESTED`, `ESCALATION_RESOLVED`, `ESCALATION_EXPIRING`. Each is a new `AgUiEvent` derived record with `[JsonDerivedType]` attribute on the polymorphic base.

`NoOpSlackNotifier : IEscalationNotificationChannel` (Infrastructure.AI) — logs at Debug level, does nothing. Extension point for consumers.

`NoOpTeamsNotifier : IEscalationNotificationChannel` (Infrastructure.AI) — same as Slack, separate class for distinct keyed DI.

**DI registration:** `CompositeEscalationNotifier` as `IEscalationNotifier` (singleton). Individual channels registered as `IEscalationNotificationChannel` entries (`AddSingleton<IEscalationNotificationChannel, AgUiEscalationNotifier>()`, etc.).

### A5. Escalation Audit Store

**Layer:** `Application.AI.Common` (interface), `Infrastructure.AI` (implementation)

**Why:** Dual persistence ensures both queryable history and real-time observability. The JSONL store is consistent with Phase 1's `JsonlDelegationStore` pattern — append-only, easy to rotate, suitable for compliance auditing.

**Interface in `Application.AI.Common/Interfaces/Escalation/`:**

`IEscalationAuditStore`:
- `RecordRequestAsync(EscalationRequest request, CancellationToken ct)`
- `RecordDecisionAsync(Guid escalationId, ApproverDecision decision, CancellationToken ct)`
- `RecordOutcomeAsync(EscalationOutcome outcome, CancellationToken ct)`
- `GetHistoryAsync(Guid escalationId, CancellationToken ct)` → `Task<IReadOnlyList<EscalationAuditRecord>>`

**Implementation:** `JsonlEscalationAuditStore` — same file-locking append pattern as `JsonlDelegationStore`. Writes to `escalations.jsonl` in a configurable data directory. Each record has a `RecordType` discriminator (Request, Decision, Outcome) for deserialization.

Additionally, the `DefaultEscalationService` logs each event through the existing `StructuredLogAuditSink` using structured logging with `EscalationConventions` tags (defined in A8) — this provides the real-time observability leg.

### A6. Governance Pipeline Integration

**Layer:** `Application.Core`

**Why:** The escalation system must be triggered automatically when the governance pipeline detects an action requiring approval. Two trigger points exist: the governance policy behavior (for tool calls) and the supervisor (for delegation autonomy violations).

**Trigger 1: GovernancePolicyBehavior — new RequireApproval branch**

The existing `GovernancePolicyBehavior<TRequest, TResponse>` MediatR pipeline behavior currently only checks `decision.IsAllowed` — everything non-allowed is treated as a denial. The `GovernancePolicyAction.RequireApproval` enum value exists but is a dead code path. Phase 2 adds a new branch: when `Action == RequireApproval`:
1. Build an `EscalationRequest` from the decision context (agent ID, tool name, matched rule, approvers list from `GovernanceDecision.Approvers`)
2. Resolve `EscalationWaitBehavior` from the agent's `AutonomyTierPolicy`
3. If `Block`: call `IEscalationService.RequestEscalationAsync()` — await the outcome, proceed or deny based on result
4. If `QueueAndContinue`: call `IEscalationService.QueueEscalationAsync()` — return a "pending approval" result immediately

**Trigger 2: Supervisor autonomy exceeded**

When `CapabilityMatchSupervisor.DelegateAsync()` returns `DelegationResult.AutonomyExceeded != null`:
1. Build an `EscalationRequest` from the `AutonomyExceededResult`
2. Route through `IEscalationService` same as Trigger 1
3. If approved, retry the delegation

### A7. Escalation Configuration

**Layer:** `Domain.Common/Config`

New config section under `AppConfig.AI.Governance.Escalation`:

`EscalationConfig`:
- `Enabled` (bool)
- `DefaultTimeoutSeconds` (int, default 300)
- `DefaultTimeoutAction` (EscalationTimeoutAction, default DenyAndEscalate)
- `DefaultApprovalStrategy` (ApprovalStrategyType, default AnyOf)
- `PriorityLevels` — dictionary of `EscalationPriority` → `EscalationPriorityConfig`

`EscalationPriorityConfig`:
- `TimeoutSeconds` (int)
- `Async` (bool) — if true, non-blocking (informational only)
- `EscalateToAll` (bool) — if true, notify all approvers simultaneously regardless of strategy

`PermissionsConfig` extended with `EscalationBehavior` per tier policy — maps `AutonomyLevel` → `EscalationWaitBehavior`.

**Existing config modifications required:**
- `GovernanceConfig` (existing class) gets a new `Escalation` property of type `EscalationConfig`
- `DefaultAutonomyTierResolver` updated to resolve `EscalationWaitBehavior` from the extended `PermissionsConfig` tier policies

### A8. OTel Instrumentation

**Layer:** `Domain.AI` (conventions), `Application.AI.Common` (metrics)

New constants in `EscalationConventions`:
- `agent.escalation.requests` — counter
- `agent.escalation.resolutions` — counter with tags: resolution_type, priority
- `agent.escalation.duration_ms` — histogram (time from request to resolution)
- `agent.escalation.timeouts` — counter
- `agent.escalation.pending` — gauge (active pending escalations)
- `agent.escalation.approver_response_ms` — histogram per approver

New `EscalationMetrics` static class with instrument instances, following the `GovernanceMetrics` and `SupervisorMetrics` pattern.

---

## Part B: Fallback Chains

### B1. Domain Models — Resilience Primitives

**Layer:** `Domain.AI`

**Why:** Fallback behavior needs its own domain vocabulary. Provider health state, fallback metadata, and degradation signals are concepts the agent interacts with, not just infrastructure concerns.

**New types in `Domain.AI/Resilience/`:**

`ProviderHealthState` enum: `Healthy`, `Degraded`, `Unavailable` — maps to circuit breaker states (Closed, HalfOpen, Open).

`FallbackMetadata` record:
- `ActiveProvider` (string) — which provider served the response
- `IsFallback` (bool) — true if not the primary provider
- `FailedProviders` (IReadOnlyList<string>)
- `DisabledCapabilities` (IReadOnlySet<string>) — features unavailable on the active provider (e.g., "tool_calling", "streaming", "vision")
- `CircuitStates` (IReadOnlyDictionary<string, ProviderHealthState>) — health of all providers for context

`ProviderExhaustedException` : Exception — thrown when all providers in the chain have failed. Contains `RetryAfter` (TimeSpan) and `FailedProviders` (IReadOnlyList<string>).

### B2. Resilient Chat Client

**Layer:** `Infrastructure.AI`

**Why:** The existing `ChatClientFactory` creates individual provider clients. We need a wrapper that composes a fallback chain with Polly v8 resilience pipeline per provider. This wrapper implements `IChatClient` so it's transparent to consumers — the `AgentExecutionContextFactory` doesn't need to change.

**Implementation in `Infrastructure.AI/Resilience/`:**

`ResilientChatClient : IChatClient`:
- Constructor takes an ordered list of `ProviderEntry` (provider name + `IChatClient` instance + `ResiliencePipeline<ChatResponse>`)
- `GetResponseAsync`: iterates through providers in order, executing through each provider's resilience pipeline. First success wins. Collects failure info for `FallbackMetadata`.
- `GetStreamingResponseAsync`: same fallback logic but streaming-aware — if primary fails before any chunks are emitted, try next provider. If primary fails mid-stream, the partial stream is discarded and next provider starts fresh (no mid-stream switching — research confirmed this is the correct approach).
- Attaches `FallbackMetadata` to the `ChatResponse.AdditionalProperties` or via a well-known extension key.

`IResilientChatClientProvider` (interface in `Application.AI.Common/Interfaces/Resilience/`):
- `GetResilientChatClientAsync(CancellationToken ct)` → `Task<IChatClient>` — returns a pre-composed `ResilientChatClient` wrapping the full provider chain
- This is a **separate interface** from `IChatClientFactory`, not a decorator. The existing `IChatClientFactory` contract is "give me a client for this specific provider," while resilient behavior is "give me a client spanning multiple providers." These are fundamentally different operations.

`ResilientChatClientProvider` (implementation in `Infrastructure.AI/Resilience/`):
- Injects `IChatClientFactory` (the original factory) and `IOptionsMonitor<ResilienceConfig>`
- Reads `FallbackChain` from config — each entry is a `FallbackProviderConfig` with `ClientType` (AIAgentFrameworkClientType) and `DeploymentId` (string), not just a provider name
- For each entry, calls `IChatClientFactory.GetChatClientAsync(clientType, deploymentId)` to get the raw client
- Wraps each with a per-provider `ResiliencePipeline<ChatResponse>` (built by `ProviderResiliencePipelineBuilder`)
- Composes them into a `ResilientChatClient`
- Caches the result (provider chain doesn't change at runtime)
- If resilience is disabled in config, returns the primary provider's raw client directly

### B3. Per-Provider Resilience Pipeline

**Layer:** `Infrastructure.AI`

**Why:** Each provider has independent failure characteristics. Azure OpenAI might be rate-limited while Anthropic is healthy. Per-provider pipelines ensure one provider's failures don't poison another's circuit breaker.

**Builder in `Infrastructure.AI/Resilience/`:**

`ProviderResiliencePipelineBuilder`:
- Takes `ResilienceConfig` (from Options pattern) and provider name
- Builds a `ResiliencePipeline<ChatResponse>` with these strategies in order:

1. **Retry** (outermost within provider) — 2 attempts, exponential backoff with jitter, handles HTTP 429/500/503, `HttpRequestException`, `TaskCanceledException`
2. **Circuit breaker** — ratio-based (0.5 failure ratio, 30s sampling window, 5 min throughput, 60s break). Each provider gets its own `CircuitBreakerStateProvider` for health reporting.
3. **Timeout** (innermost) — 30s per attempt

The fallback across providers is NOT a Polly fallback strategy — it's implemented in `ResilientChatClient.GetResponseAsync()` as a provider iteration loop. This is cleaner than nesting Polly fallbacks because:
- Each provider has its own distinct resilience pipeline
- The fallback logic needs to collect metadata across providers
- The research confirmed that Polly fallback is designed for single-provider scenarios, not multi-provider chains

**Streaming resilience:** `GetStreamingResponseAsync` returns `IAsyncEnumerable<ChatResponseUpdate>`, which cannot be wrapped in `ResiliencePipeline<ChatResponse>`. For streaming, the resilience pipeline wraps the *initiation* of the stream (the `GetStreamingResponseAsync` call itself) using a non-generic `ResiliencePipeline`. If initiation fails, try next provider. If streaming starts then fails mid-stream, discard the partial stream and retry from scratch on the next provider. Retry/circuit breaker apply to initiation, not individual chunks.

**NuGet dependency:** `Polly.Core` must be added to `Directory.Packages.props` and a `PackageReference` added to `Infrastructure.AI.csproj` (currently only `Infrastructure.APIAccess` references Polly).

### B4. Provider Health Monitor

**Layer:** `Application.AI.Common` (interface), `Infrastructure.AI` (implementation)

**Why:** OTel metrics, dashboard display, and pre-warm probes all need access to circuit breaker state. The health monitor exposes this through a clean interface.

**Interface in `Application.AI.Common/Interfaces/Resilience/`:**

`IProviderHealthMonitor`:
- `GetProviderHealth(string providerName)` → `ProviderHealthState`
- `GetAllProviderHealth()` → `IReadOnlyDictionary<string, ProviderHealthState>`
- `IsAnyProviderHealthy()` → `bool`
- `OnCircuitStateChanged` event or callback — for OTel gauge updates

**Implementation in `Infrastructure.AI/Resilience/`:**

`PollyProviderHealthMonitor`:
- Holds references to each provider's `CircuitBreakerStateProvider`
- Maps Polly `CircuitState` to `ProviderHealthState`: Closed → Healthy, HalfOpen → Degraded, Open/Isolated → Unavailable
- No synthetic pre-warm probes (LLM API calls cost tokens and there's no lightweight health endpoint). Instead, rely on Polly's default half-open behavior: when a circuit transitions to HalfOpen, the next real request serves as the recovery probe. The monitor tracks and exposes the transition for OTel metrics.

### B5. Degraded Mode — Retry Queue

**Layer:** `Infrastructure.AI`

**Why:** When all providers are exhausted, we don't want to simply fail. The graceful behavior is: return a structured error immediately, queue the request, and automatically retry when any provider recovers.

**Implementation in `Infrastructure.AI/Resilience/`:**

`LlmRetryQueue`:
- In-memory `ConcurrentQueue<QueuedLlmRequest>` with configurable max size (default 100) and TTL (default 5 minutes)
- `QueuedLlmRequest` record: original messages, options, `TaskCompletionSource<ChatResponse>`, enqueue timestamp, expiry
- Background task: monitors `IProviderHealthMonitor.OnCircuitStateChanged`. When any provider transitions to Healthy, drain the queue through `ResilientChatClient`
- TTL enforcement: periodic sweep removes expired entries, completing their TCS with `ProviderExhaustedException`
- Callers can choose to await the TCS (blocking) or fire-and-forget (best effort)
- Before retrying a queued request, check the original caller's `CancellationToken` — skip expired/cancelled requests to avoid wasting LLM tokens on abandoned work

**Registration:** `IHostedService` for the background drain/sweep task. **Conditionally registered** — only when `ResilienceConfig.Enabled == true`. If resilience is disabled, the hosted service is not registered at all.

### B6. Fallback Configuration

**Layer:** `Domain.Common/Config`

New config section under `AppConfig.AI.Resilience`:

`ResilienceConfig`:
- `Enabled` (bool)
- `FallbackChain` (`FallbackProviderConfig[]`) — ordered list of provider entries, each with `ClientType` (AIAgentFrameworkClientType) and `DeploymentId` (string) to map directly to `IChatClientFactory.GetChatClientAsync()` parameters
- `CircuitBreakerConfig` — FailureRatio, SamplingDurationSeconds, MinimumThroughput, BreakDurationSeconds
- `RetryConfig` — MaxAttempts, BaseDelaySeconds, BackoffType
- `TimeoutConfig` — PerAttemptSeconds
- `DegradedModeConfig` — RetryQueueTtlSeconds, MaxQueueSize

`FallbackProviderConfig`:
- `ClientType` (AIAgentFrameworkClientType) — which provider SDK to use
- `DeploymentId` (string) — model deployment name for this provider
- `Capabilities` (`ProviderCapabilitiesConfig`) — optional, declares what this provider supports (SupportsToolCalling, SupportsStreaming, SupportsVision, MaxTokens)

**Existing config modifications required:**
- `AIConfig` (existing class at `Domain.Common/Config/AI/AIConfig.cs`) gets a new `Resilience` property of type `ResilienceConfig`

### B7. Provider Capability Registry

**Layer:** `Infrastructure.AI`

**Why:** Different LLM providers support different features. When falling back from Azure OpenAI to Anthropic, tool calling schemas may differ or features like vision may be unavailable. The agent needs to know what's available so it can adapt.

**Implementation in `Infrastructure.AI/Resilience/`:**

`ProviderCapabilityRegistry`:
- **Config-driven** mapping of provider name → `ProviderCapabilities` record, sourced from `FallbackProviderConfig.Capabilities` in `ResilienceConfig.FallbackChain`. Template consumers declare capabilities without code changes.
- `ProviderCapabilities`: `SupportsToolCalling`, `SupportsStreaming`, `SupportsVision`, `MaxTokens`, `SupportedMediaTypes`
- When `ResilientChatClient` falls back to a provider, it populates `FallbackMetadata.DisabledCapabilities` by diffing primary vs. active provider capabilities
- If capabilities are not declared for a provider, assumes full capability (no restrictions)

### B8. Integration with AgentExecutionContextFactory

**Layer:** `Application.AI.Common`

**Why:** The `AgentExecutionContextFactory` currently resolves chat clients via `IChatClientFactory`. We need agents to transparently get resilient clients without changing the factory interface.

**Approach:** `AgentExecutionContextFactory` injects `IResilientChatClientProvider` alongside `IChatClientFactory`. When building an agent execution context:
1. If `ResilienceConfig.Enabled` and `IResilientChatClientProvider` is registered: resolve chat client via `IResilientChatClientProvider.GetResilientChatClientAsync()`
2. Otherwise: resolve via `IChatClientFactory.GetChatClientAsync()` as before

This keeps `IChatClientFactory` unchanged (its contract is "give me a client for this specific provider") and adds resilience as an opt-in capability at the context factory level. Admin operations and non-agent code continue using `IChatClientFactory` directly without resilience wrapping.

### B9. OTel Instrumentation

**Layer:** `Domain.AI` (conventions), `Application.AI.Common` (metrics)

New constants in `ResilienceConventions`:
- `agent.resilience.fallback.activations` — counter per provider switch
- `agent.resilience.circuit.state_changes` — counter per provider, per transition type
- `agent.resilience.circuit.state` — gauge per provider (0=healthy, 1=degraded, 2=unavailable)
- `agent.resilience.retry.attempts` — counter per provider
- `agent.resilience.provider.duration_ms` — histogram per provider
- `agent.resilience.degradation.events` — counter for full exhaustion
- `agent.resilience.queue.size` — gauge for retry queue depth
- `agent.resilience.queue.expired` — counter for TTL-expired requests

New `ResilienceMetrics` static class following the established pattern.

---

## Part C: Cross-Cutting Concerns

### C1. DI Registration

**Infrastructure.AI `DependencyInjection.cs`:**
- Register `IEscalationService` → `DefaultEscalationService` (singleton — manages in-memory state)
- Register `IEscalationAuditStore` → `JsonlEscalationAuditStore` (singleton)
- Register `IEscalationNotifier` → `CompositeEscalationNotifier` (singleton)
- Register `IEscalationNotificationChannel` entries: `NoOpSlackNotifier`, `NoOpTeamsNotifier`
- Register `IApprovalStrategy` keyed by `ApprovalStrategyType` (AnyOf, AllOf, Quorum) — in Application.Core DI
- Register `IProviderHealthMonitor` → `PollyProviderHealthMonitor` (singleton)
- Register `IResilientChatClientProvider` → `ResilientChatClientProvider` (singleton)
- Conditionally register `LlmRetryQueue` as `IHostedService` only when `ResilienceConfig.Enabled == true`
- Bind `EscalationConfig` and `ResilienceConfig` from Options

**Presentation.AgentHub:**
- Register `AgUiEscalationNotifier` as `IEscalationNotificationChannel` entry
- Add new AG-UI event type registrations

**Application.Core `DependencyInjection.cs`:**
- Register `IApprovalStrategy` keyed by `ApprovalStrategyType` (AnyOf, AllOf, Quorum)
- Update `GovernancePolicyBehavior` registration to inject `IEscalationService`
- Register FluentValidation validators for `EscalationConfig` and `ResilienceConfig`

### C2. Config Binding

Add to both `appsettings.json` files (AgentHub + ConsoleUI):

```
AppConfig.AI.Governance.Escalation.*
AppConfig.AI.Resilience.*
```

Bind in Options pattern via existing `AppConfig` hierarchy. `EscalationConfig` and `ResilienceConfig` as new classes in `Domain.Common/Config/AI/`.

### C3. Testing Strategy

**Unit tests (Application.Core.Tests):**
- `AnyOfApprovalStrategyTests` — single approve resolves, single deny resolves, no decisions doesn't resolve
- `AllOfApprovalStrategyTests` — all approve resolves, single deny resolves immediately, partial doesn't resolve
- `QuorumApprovalStrategyTests` — quorum met resolves, quorum impossible denies, edge cases (1-of-1, 2-of-3)
- `EscalationConfigValidatorTests` — non-negative timeouts, valid enum values, priority config consistency
- `ResilienceConfigValidatorTests` — non-empty fallback chain, valid ratio ranges, quorum threshold bounds

**Unit tests (Infrastructure.AI.Tests):**
- `DefaultEscalationServiceTests` — request creates escalation, decision triggers strategy evaluation, timeout fires deny+escalate, concurrent decisions are thread-safe, caller cancellation propagates to escalation
- `JsonlEscalationAuditStoreTests` — write/read round-trip, append-only semantics, concurrent writes
- `CompositeEscalationNotifierTests` — fans out to all channels, individual channel failure doesn't block others
- `ResilientChatClientTests` — primary succeeds (no fallback), primary fails → secondary succeeds, all fail → `ProviderExhaustedException`, circuit breaker state respected, fallback metadata populated, streaming fallback on initiation failure
- `ProviderResiliencePipelineTests` — retry on 429, circuit opens on failure ratio, timeout cancels attempt
- `PollyProviderHealthMonitorTests` — state mapping, half-open transitions
- `LlmRetryQueueTests` — enqueue/drain cycle, TTL expiry, max size enforcement, cancelled caller requests skipped on drain

**Integration tests:**
- `GovernancePolicyBehavior` with `RequireApproval` action triggers escalation service (new branch in existing behavior)
- `ResilientChatClient` with mock `IChatClient` instances simulating provider failures

### C4. File Structure

```
src/Content/Domain/Domain.AI/
  Escalation/
    EscalationRequest.cs
    EscalationOutcome.cs
    ApproverDecision.cs
    EscalationPriority.cs
    EscalationWaitBehavior.cs
    EscalationTimeoutAction.cs
    EscalationResolutionType.cs
    ApprovalStrategyType.cs
    ApprovalEvaluation.cs
    EscalationAuditRecord.cs
    EscalationAuditRecordType.cs
  Resilience/
    ProviderHealthState.cs
    FallbackMetadata.cs
    ProviderExhaustedException.cs
  Telemetry/Conventions/
    EscalationConventions.cs
    ResilienceConventions.cs

src/Content/Application/Application.AI.Common/
  Interfaces/Escalation/
    IEscalationService.cs
    IApprovalStrategy.cs
    IEscalationNotifier.cs
    IEscalationNotificationChannel.cs
    IEscalationAuditStore.cs
  Interfaces/Resilience/
    IResilientChatClientProvider.cs
    IProviderHealthMonitor.cs
  OpenTelemetry/Metrics/
    EscalationMetrics.cs
    ResilienceMetrics.cs

src/Content/Application/Application.Core/
  Escalation/Strategies/
    AnyOfApprovalStrategy.cs
    AllOfApprovalStrategy.cs
    QuorumApprovalStrategy.cs

src/Content/Domain/Domain.Common/Config/AI/
  Governance/
    EscalationConfig.cs
    EscalationPriorityConfig.cs
  Resilience/
    ResilienceConfig.cs
    FallbackProviderConfig.cs
    ProviderCapabilitiesConfig.cs
    CircuitBreakerConfig.cs
    RetryConfig.cs
    TimeoutConfig.cs
    DegradedModeConfig.cs

src/Content/Infrastructure/Infrastructure.AI/
  Escalation/
    DefaultEscalationService.cs
    JsonlEscalationAuditStore.cs
    NoOpSlackNotifier.cs
    NoOpTeamsNotifier.cs
    CompositeEscalationNotifier.cs
  Resilience/
    ResilientChatClient.cs
    ResilientChatClientProvider.cs
    ProviderResiliencePipelineBuilder.cs
    PollyProviderHealthMonitor.cs
    ProviderCapabilityRegistry.cs
    LlmRetryQueue.cs

src/Content/Presentation/Presentation.AgentHub/
  AgUi/
    (extend AgUiEventType.cs and AgUiEvents.cs with escalation events)
  Notifications/
    AgUiEscalationNotifier.cs

src/Content/Tests/Application.Core.Tests/
  Escalation/Strategies/
    AnyOfApprovalStrategyTests.cs
    AllOfApprovalStrategyTests.cs
    QuorumApprovalStrategyTests.cs
  Validation/
    EscalationConfigValidatorTests.cs
    ResilienceConfigValidatorTests.cs

src/Content/Tests/Infrastructure.AI.Tests/
  Escalation/
    DefaultEscalationServiceTests.cs
    JsonlEscalationAuditStoreTests.cs
    CompositeEscalationNotifierTests.cs
  Resilience/
    ResilientChatClientTests.cs
    ProviderResiliencePipelineTests.cs
    PollyProviderHealthMonitorTests.cs
    LlmRetryQueueTests.cs
```

---

## Implementation Order

The sections should be implemented in this order due to dependencies:

1. **Domain escalation models** (A1) — everything else references these types
2. **Domain resilience models** (B1) — same reasoning
3. **OTel conventions and metrics** (A8, B9) — needed by implementations
4. **Configuration classes + FluentValidation** (A7, B6) — needed by implementations; validators catch invalid config at startup
5. **Approval strategies** (A2) — needed by escalation service
6. **Escalation interfaces** (A3, A4, A5 interfaces) — define contracts before implementation
7. **Resilience interfaces** (B4, B8 interfaces) — define `IProviderHealthMonitor` and `IResilientChatClientProvider`
8. **Escalation service implementation** (A3 impl) — core escalation logic with cancellation propagation
9. **Escalation audit store** (A5 impl) — JSONL persistence
10. **Notification adapters** (A4 impls) — CompositeEscalationNotifier + IEscalationNotificationChannel adapters (AG-UI, no-op stubs)
11. **Per-provider resilience pipeline** (B3) — Polly composition (add `Polly.Core` package first)
12. **Resilient chat client** (B2) — provider fallback chain with streaming resilience
13. **Provider health monitor** (B4 impl) — circuit state tracking (no synthetic probes)
14. **Provider capability registry** (B7) — config-driven capability diffing
15. **Retry queue** (B5) — degraded mode with cancellation checking
16. **Resilient chat client provider** (B8) — `IResilientChatClientProvider` wrapping the chain
17. **Governance pipeline integration** (A6) — new RequireApproval branch in GovernancePolicyBehavior + supervisor trigger
18. **AG-UI event types** (Presentation) — dashboard delivery + AgUiEscalationNotifier
19. **DI registration** (C1) — wire everything, extend GovernanceConfig + AIConfig with new properties
20. **Configuration in appsettings** (C2) — config values with FallbackProviderConfig entries
21. **Tests** (C3) — full test suite (approval strategies in Application.Core.Tests, infrastructure tests in Infrastructure.AI.Tests)
