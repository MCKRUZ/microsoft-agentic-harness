# Phase 2: Enterprise Trust — Human Escalation + Fallback Chains

## Context

This is Phase 2 of the 4-phase Platform Gaps Roadmap on branch `feat/agt-governance-integration`. Phase 1 (Autonomy Tiers + Supervisor Agent) was committed in `8edf626`. Phase 2 depends on Phase 1's autonomy tiers to know WHEN to escalate.

## Gaps to Close

### Gap 3: Human Escalation (55% → 100%)

**Current state:**
- `GovernanceApprovalWorkflow` stub exists with MAF `RequestPort<ApprovalRequest, ApprovalResponse>`
- `CreateApprovalRequestExecutor` and `ProcessApprovalOutcomeExecutor` stubs exist
- `GovernanceWorkflowTypes` defines request/response shapes
- `AutonomyLevel` enum and `AutonomyTierPolicy` govern when escalation triggers
- `IAutonomyTierResolver` resolves tier policies from config

**Remaining work:**
- Real approval request/response flow end-to-end
- Escalation triggers from autonomy tiers (when `AutonomyLevel` exceeds agent's allowed tier)
- Timeout/expiry handling for pending approvals (configurable per-tier)
- Notification channels: SignalR push to dashboard (primary), extensible adapter pattern for Slack/Teams/email
- Approval audit trail (structured log + optional persistent store)
- Multi-approver support (any-of, all-of, quorum policies)
- Escalation priority levels (informational, blocking, critical)

### Gap 4: Fallback Chains (25% → 100%)

**Current state:**
- Polly NuGet package imported in `Directory.Packages.props`
- `IChatClientFactory` is the integration point for LLM provider resolution
- `DefaultHttpClientHandler` exists for HTTP resilience
- Multiple LLM providers configured in `appsettings.json` (Azure OpenAI, Anthropic, local)

**Remaining work:**
- LLM provider fallback chain (primary → secondary → tertiary) with ordered preference
- Circuit breaker per provider (Polly `CircuitBreakerPolicy` with configurable thresholds)
- Retry with exponential backoff per attempt (before moving to next provider in chain)
- Degraded mode: reduce agent capabilities gracefully when all primary providers fail
- Health checking: periodic provider health probes, pre-warmed circuit state
- Fallback telemetry/metrics: track provider switches, circuit breaks, degradation events
- Integration with existing `IChatClientFactory` and `AgentExecutionContextFactory`

## Architecture Constraints

- Clean Architecture: Domain models first, Application interfaces, Infrastructure implementations
- All config via Options pattern (`IOptionsMonitor<T>`) in `Domain.Common/Config/`
- DI registration in each layer's `DependencyInjection.cs`
- MediatR pipeline behaviors for cross-cutting concerns
- OpenTelemetry metrics/traces following existing `SupervisorConventions` pattern
- Immutability: records for DTOs/events, `IReadOnlyList<T>` for public surfaces

## Key Existing Code

- `src/Content/Application/Application.Core/Workflows/Governance/` — approval workflow stubs
- `src/Content/Domain/Domain.AI/Governance/` — autonomy levels and tier policies
- `src/Content/Application/Application.AI.Common/Interfaces/Governance/` — IAutonomyTierResolver
- `src/Content/Application/Application.AI.Common/Factories/AgentExecutionContextFactory.cs` — agent construction
- `src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs` — current DI registration
- `src/Content/Domain/Domain.Common/Config/` — config hierarchy
- `src/Content/Presentation/Presentation.AgentHub/` — SignalR hub for dashboard

## Success Criteria

1. An agent exceeding its autonomy tier automatically triggers an escalation request
2. Escalation requests are delivered to human operators via SignalR (with extensible notification adapters)
3. Pending escalations have configurable timeouts with auto-deny/auto-approve policies
4. LLM calls automatically failover through a configured provider chain
5. Circuit breakers prevent cascading failures when a provider is down
6. Degraded mode preserves core functionality when all preferred providers fail
7. Full OTel observability: escalation events, provider switches, circuit state changes
8. 80%+ test coverage on all new code
