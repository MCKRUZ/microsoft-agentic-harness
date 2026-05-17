# Section 10: Plan Step Executors

## Overview

This section implements all five keyed `IPlanStepExecutor` registrations that the `PlanExecutor` (Section 11) dispatches to. Each executor handles one `StepType` and is registered via keyed DI on the `StepType` enum value. Together they cover: LLM conversations, sandboxed tool execution, human-in-the-loop gating, conditional branching, and recursive sub-plan invocation.

The executors live in `Infrastructure.AI/Planner/StepExecutors/` because they depend on external services (MediatR dispatch, sandbox executors, escalation service, DI scope creation). They consume only Application-layer interfaces and Domain types -- no Presentation references.

### Dependencies

- **Section 02 (Application Interfaces)**: `IPlanStepExecutor` interface (the contract each executor implements), `ISandboxExecutor`, `IAttestationService`, `ICapabilityEnforcer`, `IPlanProgressNotifier`, `IPlanStateStore`
- **Section 06 (Capability Model)**: `ICapabilityEnforcer.ResolveProfileAsync` for tool permission resolution in `ToolUseStepExecutor`
- **Section 07 (Process Sandbox)**: `ProcessSandboxExecutor` as one keyed `ISandboxExecutor` implementation
- **Section 08 (Docker Sandbox)**: `DockerSandboxExecutor` as the other keyed `ISandboxExecutor` implementation
- **Section 09 (Attestation)**: `IAttestationService` for signing and verifying tool execution results

This section **blocks** Section 11 (Plan Executor), which orchestrates the executors.

---

## Domain Types Consumed

These types are defined in Section 01 and Section 02. Listed here for implementer reference:

| Type | Namespace | Role |
|------|-----------|------|
| `PlanStep` | `Domain.AI.Planner` | Step definition passed to `ExecuteAsync` |
| `PlanStepId`, `PlanId` | `Domain.AI.Planner` | Strongly-typed identifiers |
| `StepType` | `Domain.AI.Planner` | Enum keying which executor handles the step |
| `StepConfiguration` (polymorphic) | `Domain.AI.Planner` | Abstract base; cast to specific subtype per executor |
| `LlmCallConfig` | `Domain.AI.Planner` | System prompt, model key, temperature, max tokens |
| `ToolUseConfig` | `Domain.AI.Planner` | Tool name, input params, isolation override |
| `HumanGateConfig` | `Domain.AI.Planner` | Escalation message, approval strategy, timeout |
| `ConditionalBranchConfig` | `Domain.AI.Planner` | Condition expression, true/false edge targets |
| `SubPlanConfig` | `Domain.AI.Planner` | Child plan ID or inline definition, context isolation |
| `StepExecutionStatus` | `Domain.AI.Planner` | Status enum (Completed, Failed, Blocked, etc.) |
| `PlanConfiguration` | `Domain.AI.Planner` | Contains `MaxSubPlanDepth` |
| `ToolPermissionProfile` | `Domain.AI.Sandbox` | Capability requirements, allowed/denied paths, min isolation |
| `SandboxIsolationLevel` | `Domain.AI.Sandbox` | None / Process / Container |
| `SandboxExecutionRequest` | `Domain.AI.Sandbox` | Request DTO for sandbox executor |
| `ResourceLimits` | `Domain.AI.Sandbox` | Memory, CPU, subprocess, disk limits |
| `ToolExecutionAttestation` | `Domain.AI.Attestation` | HMAC-signed attestation record |

### StepExecutionResult

This type is returned by `IPlanStepExecutor.ExecuteAsync`. It is a domain record defined in `Domain.AI/Planner/`:

```csharp
public record StepExecutionResult
{
    public required StepExecutionStatus Status { get; init; }
    public string? Output { get; init; }
    public string? ErrorMessage { get; init; }
    public ToolExecutionAttestation? Attestation { get; init; }
    public string? ActiveEdgeTarget { get; init; } // For conditional branch: which edge to follow
}
```

If this type was not already created in Section 01, it must be added to `src/Content/Domain/Domain.AI/Planner/StepExecutionResult.cs` before the executors can compile.

---

## Existing Codebase Patterns

Implementers need to understand these existing patterns that the executors integrate with:

### RunConversationCommand (used by LlmCallStepExecutor)

Located at `src/Content/Application/Application.Core/CQRS/Agents/RunConversation/RunConversationCommand.cs`. Takes `AgentName`, `UserMessages`, `MaxTurns`, optional `OnProgress` callback, and `ConversationId`. Returns `ConversationResult` with `Success`, `Turns`, `FinalResponse`, `TotalToolInvocations`, `Error`. The `LlmCallStepExecutor` delegates to this command via MediatR `ISender.Send()`.

### IEscalationService (used by HumanGateStepExecutor)

Located at `src/Content/Application/Application.AI.Common/Interfaces/Escalation/IEscalationService.cs`. The `QueueEscalationAsync` method is the non-blocking path -- it returns a `Guid` escalation ID immediately. The `HumanGateStepExecutor` must use this method (not `RequestEscalationAsync` which blocks). `EscalationRequest` requires: `EscalationId`, `AgentId`, `ToolName`, `Arguments`, `Description`, `RiskLevel`, `Priority`, `Approvers`, `RequestedAt`.

### DecisionRule (used by ConditionalBranchStepExecutor)

Located at `src/Content/Domain/Domain.Common/Workflow/DecisionRule.cs`. Has `Condition` (expression string), `Outcome`, and `Validate()`. The `ConditionalBranchStepExecutor` reuses this evaluation pattern for condition expressions. Condition expressions support comparisons, AND/OR, and parentheses. Only JSON path comparisons, boolean operators, and null checks are permitted -- no arbitrary code evaluation.

### IServiceScopeFactory (used by SubPlanStepExecutor)

Used in `RunOrchestratedTaskCommandHandler` to create isolated DI scopes for sub-agent execution. The `SubPlanStepExecutor` follows the same pattern: `_scopeFactory.CreateScope()`, resolve a fresh `IPlanExecutor` from the child scope, execute the child plan.

---

## File Locations

### Production Code

| File | Project | Purpose |
|------|---------|---------|
| `Infrastructure.AI/Planner/StepExecutors/LlmCallStepExecutor.cs` | Infrastructure.AI | Delegates to `RunConversationCommand` |
| `Infrastructure.AI/Planner/StepExecutors/ToolUseStepExecutor.cs` | Infrastructure.AI | Routes through sandbox, verifies attestation |
| `Infrastructure.AI/Planner/StepExecutors/HumanGateStepExecutor.cs` | Infrastructure.AI | Non-blocking escalation, transitions to Blocked |
| `Infrastructure.AI/Planner/StepExecutors/ConditionalBranchStepExecutor.cs` | Infrastructure.AI | Evaluates condition, activates correct edge |
| `Infrastructure.AI/Planner/StepExecutors/SubPlanStepExecutor.cs` | Infrastructure.AI | Isolated scope, depth limit, child plan execution |

### Test Code

| File | Project | Purpose |
|------|---------|---------|
| `Infrastructure.AI.Tests/Planner/StepExecutors/LlmCallStepExecutorTests.cs` | Tests | LLM delegation tests |
| `Infrastructure.AI.Tests/Planner/StepExecutors/ToolUseStepExecutorTests.cs` | Tests | Sandbox routing + attestation tests |
| `Infrastructure.AI.Tests/Planner/StepExecutors/HumanGateStepExecutorTests.cs` | Tests | Non-blocking escalation tests |
| `Infrastructure.AI.Tests/Planner/StepExecutors/ConditionalBranchStepExecutorTests.cs` | Tests | Expression evaluation tests |
| `Infrastructure.AI.Tests/Planner/StepExecutors/SubPlanStepExecutorTests.cs` | Tests | Scope isolation + depth limit tests |

---

## Tests (Write First)

All tests in `src/Content/Tests/Infrastructure.AI.Tests/Planner/StepExecutors/`. The test project already has xUnit, Moq, and FluentAssertions.

### LlmCallStepExecutorTests.cs

```csharp
namespace Infrastructure.AI.Tests.Planner.StepExecutors;

public sealed class LlmCallStepExecutorTests
{
    // Test: Execute_ValidConfig_DelegatesToRunConversationCommand
    //   Arrange: PlanStep with StepType.LlmCall, LlmCallConfig containing agent name + system prompt.
    //            upstreamOutputs has one entry (simulating input from prior step).
    //            Mock ISender.Send<ConversationResult> returning success with FinalResponse "answer".
    //   Act: call ExecuteAsync
    //   Assert: ISender.Send called once with RunConversationCommand where AgentName matches config,
    //           UserMessages includes upstream output. Result.Status == Completed, Result.Output == "answer".

    // Test: Execute_StreamsTokens_NotifiesProgressNotifier
    //   Arrange: LlmCallConfig. Mock ISender returning success.
    //            Capture the OnProgress callback from RunConversationCommand.
    //   Act: call ExecuteAsync, invoke the captured OnProgress with TurnProgress data.
    //   Assert: IPlanProgressNotifier.NotifyStepStartedAsync called once,
    //           NotifyStepCompletedAsync called once with Completed status.

    // Test: Execute_LlmFailure_ReturnsFailedResult
    //   Arrange: Mock ISender returning ConversationResult with Success = false, Error = "LLM error".
    //   Act: call ExecuteAsync
    //   Assert: Result.Status == Failed, Result.ErrorMessage contains "LLM error".
}
```

### ToolUseStepExecutorTests.cs

```csharp
namespace Infrastructure.AI.Tests.Planner.StepExecutors;

public sealed class ToolUseStepExecutorTests
{
    // Test: Execute_ValidTool_RoutesToSandbox
    //   Arrange: ToolUseConfig with tool name "calculator" and input params.
    //            ICapabilityEnforcer.ResolveProfileAsync returns profile with MinimumIsolation = Process.
    //            Mock keyed ISandboxExecutor(Process) returning SandboxExecutionResult with Success = true.
    //            Mock IAttestationService.VerifyAsync returning true.
    //   Act: call ExecuteAsync
    //   Assert: ISandboxExecutor.ExecuteAsync called with correct SandboxExecutionRequest.
    //           Result.Status == Completed, Result.Attestation is not null.

    // Test: Execute_AttestationVerificationFails_ReturnsFailedResult
    //   Arrange: Same as above but IAttestationService.VerifyAsync returns false.
    //   Act: call ExecuteAsync
    //   Assert: Result.Status == Failed, ErrorMessage contains "tamper" or "verification failed".

    // Test: Execute_SandboxTimeout_ReturnsFail
    //   Arrange: Mock ISandboxExecutor returning SandboxExecutionResult with Success = false, Error = "timeout".
    //   Act: call ExecuteAsync
    //   Assert: Result.Status == Failed, ErrorMessage contains "timeout".

    // Test: Execute_IsolationElevation_SupervisedTierUsesContainer
    //   Arrange: ToolUseConfig. Profile MinimumIsolation = Process.
    //            PlanStep.RequiredAutonomyLevel = Supervised (or lower).
    //   Act: call ExecuteAsync
    //   Assert: SandboxExecutionRequest.IsolationLevel == Container (elevated).
    //           Container-keyed ISandboxExecutor was called, not Process-keyed.
}
```

### HumanGateStepExecutorTests.cs

```csharp
namespace Infrastructure.AI.Tests.Planner.StepExecutors;

public sealed class HumanGateStepExecutorTests
{
    // Test: Execute_QueuesEscalation_TransitionsToBlocked
    //   Arrange: PlanStep with HumanGateConfig (message, AnyOf strategy, 2 approvers).
    //            Mock IEscalationService.QueueEscalationAsync returning Guid.
    //   Act: call ExecuteAsync
    //   Assert: QueueEscalationAsync called once. Result.Status == Blocked.
    //           Result.Output contains the escalation ID for later polling.

    // Test: Execute_DoesNotCallRequestEscalationAsync
    //   Arrange: Same as above.
    //   Act: call ExecuteAsync
    //   Assert: IEscalationService.RequestEscalationAsync was NEVER called (blocking mode forbidden).

    // Test: Execute_ApprovedEscalation_TransitionsToCompleted
    //   Note: This tests the "resolution" path -- called by PlanExecutor when polling detects resolution.
    //   Arrange: Mock IEscalationService.GetPendingEscalationAsync returning null (resolved).
    //            Mock SubmitDecisionAsync or simulate the outcome being IsApproved = true.
    //   Assert: Step transitions to Completed with approval details as output.

    // Test: Execute_RejectedEscalation_TransitionsToFailed
    //   Arrange: Same pattern, but outcome.IsApproved = false.
    //   Assert: Result.Status == Failed, ErrorMessage indicates rejection.
}
```

### ConditionalBranchStepExecutorTests.cs

```csharp
namespace Infrastructure.AI.Tests.Planner.StepExecutors;

public sealed class ConditionalBranchStepExecutorTests
{
    // Test: Execute_TrueCondition_ActivatesTrueEdge
    //   Arrange: ConditionalBranchConfig with condition "score >= 85".
    //            upstreamOutputs containing JSON: {"score": 90}.
    //   Act: call ExecuteAsync
    //   Assert: Result.Status == Completed, Result.ActiveEdgeTarget == config.TrueEdgeTarget.

    // Test: Execute_FalseCondition_ActivatesFalseEdge
    //   Arrange: Same config, upstreamOutputs: {"score": 50}.
    //   Act: call ExecuteAsync
    //   Assert: Result.Status == Completed, Result.ActiveEdgeTarget == config.FalseEdgeTarget.

    // Test: Execute_InvalidExpression_ReturnsFailedResult
    //   Arrange: ConditionalBranchConfig with condition "INVALID SYNTAX !!!".
    //   Act: call ExecuteAsync
    //   Assert: Result.Status == Failed, ErrorMessage mentions expression evaluation failure.

    // Test: Execute_UsesDecisionRulePattern_NotCustomEvaluator
    //   Arrange: Condition using supported DecisionRule syntax: "score >= 85 AND critical == 0".
    //   Act: call ExecuteAsync
    //   Assert: Evaluation succeeds -- proves the DecisionRule pattern is reused, not a custom parser.

    // Test: Execute_InjectionAttempt_RejectedBySanitization
    //   Arrange: Condition containing unsafe content: "System.IO.File.Delete(\"*\")".
    //   Act: call ExecuteAsync
    //   Assert: Result.Status == Failed, ErrorMessage indicates unsafe expression rejected.
}
```

### SubPlanStepExecutorTests.cs

```csharp
namespace Infrastructure.AI.Tests.Planner.StepExecutors;

public sealed class SubPlanStepExecutorTests
{
    // Test: Execute_CreatesNewDiScope_IsolatesContext
    //   Arrange: SubPlanConfig with child plan ID. Mock IServiceScopeFactory.
    //            Mock child IPlanExecutor.ExecuteAsync returning success.
    //   Act: call ExecuteAsync
    //   Assert: IServiceScopeFactory.CreateScope called once.
    //           Child IPlanExecutor resolved from the child scope, not the parent.

    // Test: Execute_ChildCompletes_ReturnsChildOutput
    //   Arrange: Mock child executor returning PlanExecutionSummary with final output "child result".
    //   Act: call ExecuteAsync
    //   Assert: Result.Status == Completed, Result.Output == "child result".

    // Test: Execute_ChildFails_ReturnsFailedResult
    //   Arrange: Mock child executor returning failed Result.
    //   Act: call ExecuteAsync
    //   Assert: Result.Status == Failed, ErrorMessage contains child failure reason.

    // Test: Execute_ExceedsMaxDepth_ReturnsFail
    //   Arrange: Current depth == MaxSubPlanDepth (e.g., 5). SubPlanConfig triggers another sub-plan.
    //   Act: call ExecuteAsync
    //   Assert: Result.Status == Failed, ErrorMessage contains "max depth" or "depth limit exceeded".
    //           Child IPlanExecutor is NEVER called.

    // Test: Execute_ChildPlan_LinkedViaParentPlanId
    //   Arrange: Parent plan has PlanId = X. SubPlanConfig has child plan ID.
    //   Act: call ExecuteAsync
    //   Assert: Child plan loaded/created with ParentPlanId == X.
}
```

---

## Implementation Details

### LlmCallStepExecutor

**File**: `src/Content/Infrastructure/Infrastructure.AI/Planner/StepExecutors/LlmCallStepExecutor.cs`

**Constructor dependencies**:
- `ISender` (MediatR) -- for dispatching `RunConversationCommand`
- `IPlanProgressNotifier` -- for step progress notifications
- `ILogger<LlmCallStepExecutor>`

**Execution flow**:

1. Cast `step.Configuration` to `LlmCallConfig`. Fail with descriptive error if cast fails.
2. Build `RunConversationCommand`:
   - `AgentName` from `LlmCallConfig.ModelDeploymentKey` (or a configured agent name mapping)
   - `UserMessages` composed from: the `LlmCallConfig.SystemPrompt` as context, plus values from `upstreamOutputs` dictionary formatted as input messages
   - `MaxTurns` from config or default
   - `OnProgress` callback wired to `IPlanProgressNotifier`
3. Send via `ISender.Send<ConversationResult>()`.
4. Map `ConversationResult` to `StepExecutionResult`:
   - If `Success == true`: Status = `Completed`, Output = `FinalResponse`
   - If `Success == false`: Status = `Failed`, ErrorMessage = `Error`

No attestation for LLM calls -- attestation is only for tool executions.

### ToolUseStepExecutor

**File**: `src/Content/Infrastructure/Infrastructure.AI/Planner/StepExecutors/ToolUseStepExecutor.cs`

**Constructor dependencies**:
- `ICapabilityEnforcer` -- resolve tool permission profile
- `IServiceProvider` -- for keyed DI resolution of `ISandboxExecutor`
- `IAttestationService` -- verify execution attestation
- `IPlanProgressNotifier` -- sandbox status notifications
- `ILogger<ToolUseStepExecutor>`

**Execution flow**:

1. Cast `step.Configuration` to `ToolUseConfig`.
2. Call `ICapabilityEnforcer.ResolveProfileAsync(config.ToolName)` to get the tool's `ToolPermissionProfile`.
3. Determine isolation level using the **never-downgrade** algorithm:
   - Start with `profile.MinimumIsolation`
   - If `config.IsolationLevelOverride` is set and higher, use that
   - If `step.RequiredAutonomyLevel` is `Supervised` or `Restricted`, elevate to `Container`
   - Final isolation is `Max(profile.MinimumIsolation, computed)` -- never below declared minimum
4. Build `SandboxExecutionRequest`:
   - `ToolName` = `config.ToolName`
   - `Input` = JSON-serialized input from `config.InputParameters` merged with relevant `upstreamOutputs`
   - `IsolationLevel` = computed above
   - `Permissions` = resolved profile
   - `ResourceLimits` = from profile or defaults
   - `Timeout` = `step.Timeout`
5. Resolve keyed `ISandboxExecutor` from DI using `IsolationLevel` as the key: `serviceProvider.GetRequiredKeyedService<ISandboxExecutor>(isolationLevel)`.
6. Call `ISandboxExecutor.ExecuteAsync()`.
7. **Verify attestation**: If `result.Attestation` is not null, call `IAttestationService.VerifyAsync(result.Attestation)`. If verification returns `false`, return `StepExecutionResult` with `Status = Failed` and `ErrorMessage = "Attestation verification failed: possible tampering detected"`.
8. Notify via `IPlanProgressNotifier.NotifySandboxStatusAsync()` with tool name, isolation level, resource usage, attestation hash.
9. Map to `StepExecutionResult`:
   - If `result.Success == true` and attestation verified: Status = `Completed`, Output = `result.Output`, Attestation = `result.Attestation`
   - If `result.Success == false`: Status = `Failed`, ErrorMessage = `result.Error`, Attestation = `result.Attestation` (may be a failure attestation)

### HumanGateStepExecutor

**File**: `src/Content/Infrastructure/Infrastructure.AI/Planner/StepExecutors/HumanGateStepExecutor.cs`

**Constructor dependencies**:
- `IEscalationService` -- for `QueueEscalationAsync` (non-blocking only)
- `IPlanProgressNotifier` -- gate status notifications
- `ILogger<HumanGateStepExecutor>`

**Execution flow**:

1. Cast `step.Configuration` to `HumanGateConfig`.
2. Build `EscalationRequest`:
   - `EscalationId` = `Guid.NewGuid()`
   - `AgentId` = "planner" (or a configurable identifier)
   - `ToolName` = step name (for display context)
   - `Arguments` = summary from `upstreamOutputs`
   - `Description` = `config.EscalationMessage`
   - `RiskLevel` = derived from step context or default `Medium`
   - `Priority` = `EscalationPriority.Normal`
   - `ApprovalStrategy` = mapped from `config.ApprovalStrategyKey` (`"AnyOf"` -> `ApprovalStrategyType.AnyOf`, etc.)
   - `Approvers` = from config or default
   - `TimeoutSeconds` = from `config.Timeout` or step timeout
   - `RequestedAt` = `DateTimeOffset.UtcNow`
3. Call `IEscalationService.QueueEscalationAsync(request)` -- this is the **non-blocking** path. Returns escalation ID immediately.
4. Notify via `IPlanProgressNotifier` with gate metadata.
5. Return `StepExecutionResult` with `Status = Blocked` and `Output` = serialized escalation ID (for the plan executor to poll later).

The plan executor (Section 11) is responsible for polling `IEscalationService.GetPendingEscalationAsync()` on each scheduling pass and transitioning the step to Completed (approved) or Failed (rejected) when the escalation resolves. The executor itself does NOT wait.

### ConditionalBranchStepExecutor

**File**: `src/Content/Infrastructure/Infrastructure.AI/Planner/StepExecutors/ConditionalBranchStepExecutor.cs`

**Constructor dependencies**:
- `ILogger<ConditionalBranchStepExecutor>`

No external service dependencies -- this executor is pure logic.

**Execution flow**:

1. Cast `step.Configuration` to `ConditionalBranchConfig`.
2. Build evaluation context from `upstreamOutputs`: deserialize JSON values into a `Dictionary<string, object>` for variable resolution.
3. Sanitize the condition expression: reject anything containing method calls, `System.`, type names, or other patterns that indicate arbitrary code. Only permit: JSON path references, comparison operators (`==`, `!=`, `>=`, `<=`, `>`, `<`), boolean operators (`AND`, `OR`, `NOT`), null checks, numeric/string literals, and parentheses.
4. Create a `DecisionRule` with `Condition = config.ConditionExpression` and `Outcome = "true"`. Evaluate against the context. The `DecisionRule` pattern from `Domain.Common/Workflow/DecisionRule.cs` provides the evaluation framework.
5. Determine result:
   - If condition evaluates to match: `ActiveEdgeTarget` = `config.TrueEdgeTarget`
   - If condition does not match: `ActiveEdgeTarget` = `config.FalseEdgeTarget`
6. Return `StepExecutionResult` with `Status = Completed` and the appropriate `ActiveEdgeTarget`.
7. The plan executor uses `ActiveEdgeTarget` to determine which downstream steps to enqueue.

**Expression safety**: Condition expressions are validated at plan creation time (Section 03) and re-validated at execution time. The whitelist approach prevents injection attacks. If an expression fails sanitization, return `Status = Failed` with a descriptive error.

### SubPlanStepExecutor

**File**: `src/Content/Infrastructure/Infrastructure.AI/Planner/StepExecutors/SubPlanStepExecutor.cs`

**Constructor dependencies**:
- `IServiceScopeFactory` -- for creating isolated child scopes
- `IPlanStateStore` -- for loading/creating child plans
- `IPlanProgressNotifier` -- for child plan progress
- `ILogger<SubPlanStepExecutor>`

**Depth tracking**: The executor receives the current depth via a `PlanExecutionContext` scoped service (aligns with the existing `AgentExecutionContext` pattern). The child scope gets a context with `Depth + 1`.

**Execution flow**:

1. Cast `step.Configuration` to `SubPlanConfig`.
2. Check depth: if current depth >= `PlanConfiguration.MaxSubPlanDepth` (default 5), return `StepExecutionResult` with `Status = Failed` and `ErrorMessage = "Maximum sub-plan depth exceeded"`. Do NOT call the child executor.
3. Create a new DI scope: `var scope = _scopeFactory.CreateScope()`.
4. Load or create the child plan:
   - If `config.ChildPlanId` is set, load from `IPlanStateStore`
   - If inline definition is provided, create a new plan with `ParentPlanId` set to the current plan's ID
5. Set up the child `PlanExecutionContext` in the scope with `Depth = currentDepth + 1`.
6. Resolve `IPlanExecutor` from the child scope.
7. Call `childExecutor.ExecuteAsync(childPlanId, cancellationToken)`.
8. Map the child's `PlanExecutionSummary` to `StepExecutionResult`:
   - Success: Status = `Completed`, Output = child plan's final output
   - Failure: Status = `Failed`, ErrorMessage = child plan's failure reason
9. Dispose the child scope.

The parent step blocks while the child plan executes, but the parent plan's executor continues running other independent branches in parallel.

---

## DI Registration (Preview)

Section 15 handles full registration, but for context, these are the keyed registrations:

```csharp
services.AddKeyedScoped<IPlanStepExecutor, LlmCallStepExecutor>(StepType.LlmCall);
services.AddKeyedScoped<IPlanStepExecutor, ToolUseStepExecutor>(StepType.ToolUse);
services.AddKeyedScoped<IPlanStepExecutor, HumanGateStepExecutor>(StepType.HumanGate);
services.AddKeyedScoped<IPlanStepExecutor, ConditionalBranchStepExecutor>(StepType.ConditionalBranch);
services.AddKeyedScoped<IPlanStepExecutor, SubPlanStepExecutor>(StepType.SubPlanInvocation);
```

The plan executor resolves executors via: `serviceProvider.GetRequiredKeyedService<IPlanStepExecutor>(step.Type)`.

---

## Implementation Checklist

1. Ensure `StepExecutionResult` exists in `Domain.AI/Planner/` (may already be in Section 01)
2. Write all 5 test files with the stubs above
3. Implement `LlmCallStepExecutor` -- simplest, delegates to existing `RunConversationCommand`
4. Implement `ConditionalBranchStepExecutor` -- pure logic, no external dependencies
5. Implement `HumanGateStepExecutor` -- thin wrapper over `IEscalationService.QueueEscalationAsync`
6. Implement `ToolUseStepExecutor` -- most complex, integrates sandbox + attestation + capability
7. Implement `SubPlanStepExecutor` -- recursive, requires scope isolation + depth tracking
8. Verify all tests pass: `dotnet test src/Content/Tests/Infrastructure.AI.Tests/ --filter "FullyQualifiedName~StepExecutors"`

---

## Implementation Notes (Actual)

### Deviations from Plan

1. **PlanExecutionContext as constructor dependency**: All executors receive `PlanExecutionContext` via DI (scoped). This provides `CurrentPlanId` for notifier calls instead of deriving it from `step.Id.PlanId()` (which doesn't exist on PlanStepId).

2. **RunConversationCommand.SystemPrompt**: Added `public string? SystemPrompt { get; init; }` to `RunConversationCommand` during code review. LlmCallStepExecutor now sets SystemPrompt on the command instead of including it in UserMessages.

3. **HumanGateConfig enriched**: Added `Approvers` (`IReadOnlyList<string>`, default: `["default-approver"]`) and `RiskLevel` (`RiskLevel`, default: `Medium`) properties during code review instead of hardcoding values in the executor.

4. **Expression security hardening**: ConditionalBranchStepExecutor has 500-char length limit, dot character rejection, and 10-level recursion depth limit on expression evaluation. Uses `AsSpan()` for O(1) character checks.

5. **IPlanExecutor context-aware overload**: Added `ExecuteAsync(PlanId, PlanExecutionContext, CancellationToken)` to IPlanExecutor. SubPlanStepExecutor passes `childContext` explicitly for proper depth enforcement.

6. **PlanExecutionContext as sealed record**: Converted from `sealed class` to `sealed record` for value-semantics immutability.

7. **ToolUseStepExecutor try-catch**: Wraps `executor.ExecuteAsync()` in try-catch to return Failed result instead of propagating sandbox exceptions.

8. **EscalationPriority.Blocking**: Used instead of planned `Normal` (which doesn't exist in the enum).

### Test Coverage

| Test File | Test Count | All Pass |
|-----------|-----------|----------|
| LlmCallStepExecutorTests | 5 | Yes |
| ConditionalBranchStepExecutorTests | 8 | Yes |
| HumanGateStepExecutorTests | 5 | Yes |
| ToolUseStepExecutorTests | 7 | Yes |
| SubPlanStepExecutorTests | 6 | Yes |
| **Total** | **31** | **Yes** |

### Files Created/Modified

**New files:**
- `src/Content/Infrastructure/Infrastructure.AI/Planner/StepExecutors/LlmCallStepExecutor.cs`
- `src/Content/Infrastructure/Infrastructure.AI/Planner/StepExecutors/ToolUseStepExecutor.cs`
- `src/Content/Infrastructure/Infrastructure.AI/Planner/StepExecutors/HumanGateStepExecutor.cs`
- `src/Content/Infrastructure/Infrastructure.AI/Planner/StepExecutors/ConditionalBranchStepExecutor.cs`
- `src/Content/Infrastructure/Infrastructure.AI/Planner/StepExecutors/SubPlanStepExecutor.cs`
- `src/Content/Tests/Infrastructure.AI.Tests/Planner/StepExecutors/LlmCallStepExecutorTests.cs`
- `src/Content/Tests/Infrastructure.AI.Tests/Planner/StepExecutors/ToolUseStepExecutorTests.cs`
- `src/Content/Tests/Infrastructure.AI.Tests/Planner/StepExecutors/HumanGateStepExecutorTests.cs`
- `src/Content/Tests/Infrastructure.AI.Tests/Planner/StepExecutors/ConditionalBranchStepExecutorTests.cs`
- `src/Content/Tests/Infrastructure.AI.Tests/Planner/StepExecutors/SubPlanStepExecutorTests.cs`

**Modified files:**
- `src/Content/Domain/Domain.AI/Planner/PlanExecutionContext.cs` (class -> record)
- `src/Content/Domain/Domain.AI/Planner/HumanGateConfig.cs` (added Approvers, RiskLevel)
- `src/Content/Application/Application.AI.Common/Interfaces/Planner/IPlanExecutor.cs` (added context overload)
- `src/Content/Application/Application.Core/CQRS/Agents/RunConversation/RunConversationCommand.cs` (added SystemPrompt)
