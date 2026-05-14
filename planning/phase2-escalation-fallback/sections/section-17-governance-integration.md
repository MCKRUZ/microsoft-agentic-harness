# Section 17: Governance Integration

## Overview

This section wires the escalation subsystem into the two governance trigger points: the `GovernancePolicyBehavior` MediatR pipeline behavior (for tool calls matching `RequireApproval` policies) and the `CapabilityMatchSupervisor` (for autonomy tier violations). It also updates `CreateApprovalRequestExecutor` to delegate to `IEscalationService`, and adds a `PendingApproval` result type for non-blocking escalation flows.

**Dependencies (must be completed first):**
- Section 08 (Escalation Service) -- provides `IEscalationService`, `DefaultEscalationService`
- Section 16 (Resilient Provider) -- provides `IResilientChatClientProvider`, `ResilientChatClientProvider`
- Section 01 (Domain Escalation) -- provides `EscalationRequest`, `EscalationOutcome`, `EscalationWaitBehavior`, `EscalationPriority`, `ApprovalStrategyType`, `EscalationTimeoutAction`, `ApproverDecision`
- Section 04 (Config and Validation) -- provides `EscalationConfig` and its `GovernanceConfig.Escalation` property
- Section 05 (Approval Strategies) -- provides `IApprovalStrategy` and keyed DI registrations
- Section 06 (Escalation Interfaces) -- provides `IEscalationService` interface definition

**Blocks:** Section 19 (DI Registration)

---

## Implementation Summary (Actual)

### Files Modified

| Action | File Path | Changes |
|--------|-----------|---------|
| Modify | `src/Content/Domain/Domain.Common/Result.cs` | Added `PendingApproval = 9` to `ResultFailureType`, factory methods on `Result` and `Result<T>` |
| Modify | `src/Content/Domain/Domain.Common/Config/AI/Permissions/AutonomyTierPolicyConfig.cs` | Added `EscalationBehavior` string property (default: `"Block"`) |
| Modify | `src/Content/Application/Application.AI.Common/MediatRBehaviors/GovernancePolicyBehavior.cs` | Added `RequireApproval` branch with `IEscalationService` integration, fail-closed error handling, tier-aware `ResolveEscalationWaitBehavior` |
| Modify | `src/Content/Infrastructure/Infrastructure.AI/Agents/CapabilityMatchSupervisor.cs` | Added escalation trigger on autonomy exceeded with pattern matching guard, fail-closed error handling |
| Modify | `src/Content/Application/Application.Core/Workflows/Governance/CreateApprovalRequestExecutor.cs` | Added `ILogger`, delegated to `IEscalationService.QueueEscalationAsync` with try/catch |
| Modify | `src/Content/Application/Application.Core/Workflows/Governance/GovernanceApprovalWorkflow.cs` | Updated factory to resolve and pass `ILogger<CreateApprovalRequestExecutor>` |
| Modify | `src/Content/Tests/Domain.Common.Tests/Enums/ResultFailureTypeTests.cs` | Updated count to 10, added `PendingApproval` inline data |
| Modify | `src/Content/Tests/Application.AI.Common.Tests/MediatRBehaviors/GovernancePolicyBehaviorTests.cs` | Added `PermissionsConfig` to all constructor calls |
| Create | `src/Content/Tests/Application.AI.Common.Tests/MediatRBehaviors/GovernancePolicyBehaviorEscalationTests.cs` | 6 tests for escalation flows |
| Create | `src/Content/Tests/Infrastructure.AI.Tests/Agents/CapabilityMatchSupervisorEscalationTests.cs` | 3 tests for supervisor escalation |
| Create | `src/Content/Tests/Application.Core.Tests/Workflows/Governance/CreateApprovalRequestExecutorTests.cs` | 3 tests for executor escalation delegation |

### Deviations from Plan

1. **`IAutonomyTierResolver` not injected into `GovernancePolicyBehavior`** — `IAgentExecutionContext` does not expose autonomy level. Instead, `ResolveEscalationWaitBehavior` uses `PermissionsConfig.DefaultAutonomyLevel` as the lookup key into `TierPolicies`. Deterministic and correct for current architecture.

2. **`RiskLevel` is an enum, not a string** — Plan assumed string. Used `RiskLevel.Medium` as default.

3. **`EscalationConfig` uses string properties** — `DefaultTimeoutAction` and `DefaultApprovalStrategy` are strings parsed via `Enum.TryParse`, not typed enums.

4. **Section 5 (AgentExecutionContextFactory resilience) already done** — `IResilientChatClientProvider?` was injected in section 16. Skipped.

5. **Added `ILogger<CreateApprovalRequestExecutor>`** — Not in original plan. Added during code review to support the try/catch warning log for failed escalation queuing. Required updating `GovernanceApprovalWorkflow.cs` factory.

6. **Added fail-closed error handling** — Code review identified no try/catch around `IEscalationService` calls. Added `try/catch` in `GovernancePolicyBehavior.HandleRequireApprovalAsync` (returns `GovernanceBlocked` on exception) and `CapabilityMatchSupervisor.HandleAutonomyEscalationAsync` (returns null → falls through to "no agent found").

7. **Replaced null-forgiving operator** — `_escalationService!` in `CapabilityMatchSupervisor` replaced with `_escalationService is not { } escalation` pattern match guard.

### Test Count

- `GovernancePolicyBehaviorEscalationTests`: 6 tests (5 original + 1 fail-closed)
- `GovernancePolicyBehaviorTests`: 7 tests (updated constructors)
- `CapabilityMatchSupervisorEscalationTests`: 3 tests
- `CreateApprovalRequestExecutorTests`: 3 tests
- `ResultFailureTypeTests`: 16 tests (updated count + PendingApproval data)
- **Total section-relevant: 35 tests, all passing**

---

## Key Design Decisions

1. **`PendingApproval` as a `ResultFailureType`:** The `QueueAndContinue` path needs to return something that is not "success" but also not "denied." Adding a new failure type keeps the Result pattern consistent.

2. **Escalation config guard:** When `GovernanceConfig.Escalation.Enabled` is false, `RequireApproval` decisions fall through to the existing denial path. This ensures the escalation system is fully opt-in.

3. **Supervisor retry-after-approval:** Uses `minimumTier > AutonomyLevel.Restricted` as the escalation guard. Retry calls `DelegateAsync` with `minimumTier = AutonomyLevel.Restricted`, which naturally prevents re-escalation (guard evaluates false).

4. **Fail-closed on escalation service errors:** All `IEscalationService` call sites catch exceptions and deny the request rather than allowing it to proceed unchecked. This is the correct security posture for governance enforcement.

5. **Tier-aware wait behavior resolution:** `ResolveEscalationWaitBehavior` uses `PermissionsConfig.DefaultAutonomyLevel` to look up the correct tier policy, not arbitrary first-match iteration.

6. **Executor does not await escalation:** `CreateApprovalRequestExecutor` queues the escalation (non-blocking) to start the timeout timer and notifications. Failure to queue logs a warning but still returns the `ApprovalRequest` (fire-and-forget intent preserved).

---

## Verification

```
dotnet build src/AgenticHarness.slnx
dotnet test src/Content/Tests/Application.AI.Common.Tests --filter "GovernancePolicyBehavior"
dotnet test src/Content/Tests/Infrastructure.AI.Tests --filter "CapabilityMatchSupervisor"
dotnet test src/Content/Tests/Application.Core.Tests --filter "CreateApprovalRequestExecutor"
dotnet test src/Content/Tests/Domain.Common.Tests --filter "ResultFailureType"
```
