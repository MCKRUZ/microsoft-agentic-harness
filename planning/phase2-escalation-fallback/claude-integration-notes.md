# Opus Review Integration Notes

## Integrating (Yes)

### 1. Reconcile with existing GovernanceApprovalWorkflow (Critical #1)
**Integrating.** The reviewer is right — the plan creates a parallel approval system. The existing `GovernanceApprovalWorkflow` with MAF `RequestPort` should be the foundation. The new escalation system extends it rather than replacing it. `IEscalationService` becomes the orchestrator that feeds into the existing workflow's `RequestPort`, not a competing mechanism.

### 2. Fix CompositeEscalationNotifier DI (Architectural #6)
**Integrating.** Real infinite recursion bug. Solution: introduce `IEscalationNotificationChannel` as the inner interface. Individual adapters implement `IEscalationNotificationChannel`. `CompositeEscalationNotifier` implements `IEscalationNotifier` and injects `IEnumerable<IEscalationNotificationChannel>`.

### 3. Separate ResilientChatClientFactory from IChatClientFactory (Architectural #7)
**Integrating.** The decorator pattern doesn't fit. New `IResilientChatClientProvider` interface with a single method `GetResilientChatClientAsync()` that returns a pre-composed `ResilientChatClient`. `AgentExecutionContextFactory` resolves this when resilience is enabled.

### 4. FallbackChain config shape (Critical #3)
**Integrating.** Change from `string[]` to structured entries: `FallbackProviderConfig[]` with `ClientType` (AIAgentFrameworkClientType) and `DeploymentId` (string). This maps directly to `IChatClientFactory.GetChatClientAsync()` parameters.

### 5. Add Polly packages (Critical #4)
**Integrating.** Add `Polly.Core` to `Directory.Packages.props` and `PackageReference` to `Infrastructure.AI.csproj`.

### 6. Streaming resilience (Design #11)
**Integrating.** `ResiliencePipeline<ChatResponse>` can't wrap `IAsyncEnumerable`. For streaming: wrap the enumerable *creation* (the `GetStreamingResponseAsync` call itself) in a non-generic `ResiliencePipeline`. If creation fails, try next provider. If streaming starts then fails mid-stream, discard and retry from scratch on next provider. The retry/circuit breaker applies to initiation, not individual stream chunks.

### 7. Move approval strategy tests (Missing #15)
**Integrating.** Tests for `Application.Core` classes go in `Application.Core.Tests`, not `Infrastructure.AI.Tests`.

### 8. Add EscalationAuditRecord (Minor #19)
**Integrating.** Add to `Domain.AI/Escalation/` — record with `RecordType` discriminator, `EscalationId`, `Timestamp`, and variant payload.

### 9. Add properties to existing config classes (Minor #21, #22)
**Integrating.** `GovernanceConfig` gets `Escalation` property. `AIConfig` gets `Resilience` property. Both listed as modifications.

### 10. FluentValidation for config (Missing #13)
**Integrating.** Add validators for `EscalationConfig` and `ResilienceConfig` in `Application.Core`. Validate: non-negative timeouts, non-empty fallback chain, quorum threshold <= approver count, valid enum values.

### 11. GovernancePolicyBehavior RequireApproval is new code (Critical #2)
**Integrating.** Correcting the plan language — this is creating a new branch in the behavior, not updating an existing one. The `RequireApproval` action enum value exists but the behavior doesn't handle it.

### 12. DefaultAutonomyTierResolver needs updating (Missing #14)
**Integrating.** Adding explicit note that `DefaultAutonomyTierResolver` must map the new `EscalationWaitBehavior` from config.

### 13. Conditional LlmRetryQueue registration (Design #10)
**Integrating.** Only register `IHostedService` when `ResilienceConfig.Enabled == true`. Check caller `CancellationToken` before retrying queued requests.

### 14. Cancellation propagation (Missing #16)
**Integrating.** Link the caller's `CancellationToken` to the escalation's `CancellationTokenSource`. If the caller cancels, the escalation timeout also cancels and the TCS completes with cancellation.

### 15. ProviderCapabilityRegistry: config-driven (Design #18)
**Integrating.** Make it config-driven so template consumers can declare provider capabilities without code changes. Add `ProviderCapabilities` section under each provider in `ResilienceConfig.FallbackChain`.

## NOT Integrating (With Reasoning)

### EscalationRequest "over-specified" (Design #8)
**Not integrating.** The reviewer suggests moving strategy/timeout/approvers out of the request and into config-resolved-at-runtime. However, the request represents a *resolved* escalation — it's the output of policy resolution, not the input. The `DefaultEscalationService` resolves config first, then constructs the request with resolved values. The request is an immutable snapshot of what was decided, not a template for future decisions. This is consistent with how `GovernanceDecision` already carries resolved `Approvers`, `MatchedRule`, etc.

### IAgentExecutionContext scoping (Design #9)
**Not integrating as a plan change.** Valid concern but the execution context is already scoped per MediatR request pipeline. For `QueueAndContinue`, the escalation service manages its own state independent of the execution context. The original context isn't needed for the queued action — when approval comes, a new context is created for the retry. This is consistent with how the existing `GovernanceApprovalWorkflow` uses stateless executors.

### Non-durable escalation state (Architectural #5)
**Partially integrating.** Adding explicit documentation of the limitation in the plan. Adding startup warning log if unresolved JSONL entries exist. NOT building durable recovery in Phase 2 — that's a Phase 3+ concern. In-memory state with audit trail is the right trade-off for this phase.

### Pre-warm probe cost (Design #12)
**Partially integrating.** Instead of sending a chat message, use Polly's default half-open behavior: the next real request serves as the probe. Drop the synthetic probe entirely. The `PollyProviderHealthMonitor` monitors state transitions but doesn't actively probe.

### SSE connection keep-alive during Block escalation (Missing #17)
**Not integrating as a plan change.** The AG-UI SSE stream already pushes `ESCALATION_REQUESTED` events, which the client uses to show a "waiting for approval" state. The SSE connection has existing timeout/keepalive configuration (120s timeouts, 30s keepalive per memory). This is sufficient — the escalation will resolve or timeout before the connection does.
