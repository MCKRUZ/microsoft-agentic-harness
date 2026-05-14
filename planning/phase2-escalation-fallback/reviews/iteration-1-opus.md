# Opus Review

**Model:** claude-opus-4
**Generated:** 2026-05-08T13:30:00Z

---

## Critical Issues

**1. Part A duplicates existing GovernanceApprovalWorkflow.** The codebase already has MAF RequestPort-based approval with `ApprovalRequest`, `ApprovalResponse`, `GovernanceApprovalOutcome`, `CreateApprovalRequestExecutor`, `ProcessApprovalOutcomeExecutor`. Plan never mentions this. Building a parallel system creates two competing approval mechanisms.

**2. GovernancePolicyBehavior has no RequireApproval handling.** Plan assumes it does. Actual code only checks `decision.IsAllowed` — everything non-allowed is denied identically. `RequireApproval` is a dead code path. Section A6 is new creation, not an update.

**3. IChatClientFactory.GetChatClientAsync takes (clientType, deploymentId), not just provider name.** `FallbackChain` as `string[]` doesn't specify deployment mapping. Needs to be list of `{ ClientType, DeploymentId }` objects.

**4. Polly NuGet missing from Infrastructure.AI.** `Polly.Core`/`Microsoft.Extensions.Resilience` only referenced by `Infrastructure.APIAccess`. Plan needs to add package to `Infrastructure.AI.csproj`.

## Architectural Issues

**5. DefaultEscalationService in-memory state is non-durable.** Process restart loses pending escalations. No recovery from JSONL audit log. Should acknowledge limitation and log warning on startup.

**6. CompositeEscalationNotifier DI creates infinite recursion.** If registered as `IEscalationNotifier` and individual notifiers also as `IEscalationNotifier`, the composite injecting `IEnumerable<IEscalationNotifier>` receives itself → infinite loop. Need different interface or keyed DI.

**7. ResilientChatClientFactory decorator doesn't fit IChatClientFactory contract.** Interface is "give me client for this specific provider" but resilient behavior is "give me client spanning multiple providers." Separate `IResilientChatClientProvider` or explicit resolution in AgentExecutionContextFactory.

## Design Concerns

**8. EscalationRequest over-specified.** Mixes domain identity with operational policy. Strategy, timeouts, approver lists should come from config at runtime, not be embedded in request.

**9. No IAgentExecutionContext scoping for escalation lifecycle.** Correlation across pipeline invocations (original call, approval wait, retry) not addressed. Especially for QueueAndContinue path.

**10. LlmRetryQueue always runs even when resilience disabled.** Should conditionally register. Also needs to check caller CancellationToken before retrying.

**11. GetStreamingResponseAsync returns IAsyncEnumerable, not ChatResponse.** ResiliencePipeline<ChatResponse> won't work. Streaming needs separate resilience approach.

**12. Pre-warm probe sends LLM inference request.** Costly. Should use lightweight health check or let Polly's default half-open behavior handle it.

## Missing Considerations

**13.** No FluentValidation for new config classes.
**14.** DefaultAutonomyTierResolver needs updating for EscalationWaitBehavior but not listed.
**15.** Approval strategy tests placed in Infrastructure.AI.Tests but implementations are in Application.Core.
**16.** No cancellation propagation for blocking escalation — zombie escalations possible.
**17.** No keep-alive/timeout handling for SSE connection during Block escalation.
**18.** ProviderCapabilityRegistry doesn't specify static vs config-driven.

## Minor Issues

**19.** `EscalationAuditRecord` referenced but never defined.
**20.** Structured logging in A5 should use `EscalationConventions`, not `GovernanceConventions`.
**21.** `GovernanceConfig` needs `Escalation` property — not called out as modification.
**22.** `AIConfig` needs `Resilience` property — not called out as modification.

## Required Plan Updates

1. Reconcile with existing GovernanceApprovalWorkflow
2. Fix CompositeEscalationNotifier DI infinite recursion
3. Redesign ResilientChatClientFactory — separate interface, not decorator
4. Specify FallbackChain as (ClientType, DeploymentId) pairs
5. Add Polly packages to Directory.Packages.props + Infrastructure.AI.csproj
6. Address streaming resilience separately from non-streaming
7. Move approval strategy tests to Application.Core.Tests
8. Add EscalationAuditRecord to domain models
9. Add Escalation/Resilience properties to existing config classes
10. Add FluentValidation for all new config classes
