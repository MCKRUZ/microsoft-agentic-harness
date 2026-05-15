# Section 03: Plan Validation

## Overview

This section implements `PlanValidator` (the sole implementation of `IPlanValidator`) with seven validation checks for `PlanGraph` objects, plus FluentValidation validators for each `StepConfiguration` subtype. Plan validation runs before plan persistence (`CreatePlanCommand`) and before execution (`ExecutePlanCommand`).

## Dependencies

- **section-01-domain-models** -- Provides all domain types: `PlanGraph`, `PlanStep`, `PlanEdge`, `PlanStepId`, `PlanId`, `StepType`, `StepConfiguration` (abstract + 5 subtypes: `LlmCallConfig`, `ToolUseConfig`, `HumanGateConfig`, `ConditionalBranchConfig`, `SubPlanConfig`), `PlanEdge`, `EdgeType`, `PlanConfiguration`, `RetryPolicy`
- **section-02-application-interfaces** -- Provides `IPlanValidator` interface in `Application.AI.Common/Interfaces/Planner/`

## File Locations

### Production Code

| File | Project | Purpose |
|------|---------|---------|
| `src/Content/Infrastructure/Infrastructure.AI/Planner/PlanValidator.cs` | Infrastructure.AI | Core validator with 7 graph-level checks |
| `src/Content/Application/Application.Core/Validation/Planner/LlmCallConfigValidator.cs` | Application.Core | FluentValidation for `LlmCallConfig` |
| `src/Content/Application/Application.Core/Validation/Planner/ToolUseConfigValidator.cs` | Application.Core | FluentValidation for `ToolUseConfig` |
| `src/Content/Application/Application.Core/Validation/Planner/HumanGateConfigValidator.cs` | Application.Core | FluentValidation for `HumanGateConfig` |
| `src/Content/Application/Application.Core/Validation/Planner/ConditionalBranchConfigValidator.cs` | Application.Core | FluentValidation for `ConditionalBranchConfig` |
| `src/Content/Application/Application.Core/Validation/Planner/SubPlanConfigValidator.cs` | Application.Core | FluentValidation for `SubPlanConfig` |

### Test Code

| File | Project | Purpose |
|------|---------|---------|
| `src/Content/Tests/Infrastructure.AI.Tests/Planner/PlanValidatorTests.cs` | Infrastructure.AI.Tests | 16 graph-level validation tests |
| `src/Content/Tests/Application.Core.Tests/Validators/Planner/StepConfigValidatorTests.cs` | Application.Core.Tests | 19 FluentValidation tests for each config subtype |

### Deviations from Plan
- **PlanValidator tests** moved to `Infrastructure.AI.Tests` (not `Application.Core.Tests`) because the implementation lives in `Infrastructure.AI` and the test project needs that reference.
- **`PlanValidationReport`** not created — reused existing `PlanValidationResult` domain type from section-01.
- **`ancestorPlanIds` parameter** not added to `IPlanValidator.ValidateAsync` — interface from section-02 uses `(PlanGraph, CancellationToken)`. Ancestor check uses `plan.ParentPlanId` (immediate parent only). Full ancestor chain validation deferred until `IPlanStateStore` is available.
- **`Validate_ToolUseConfig_UnregisteredToolKey`** renamed to `EmptyToolName` — tool registry doesn't exist yet (section-10). Tests FluentValidation NotEmpty rule instead.
- **SubPlanConfig validator** uses XOR (`^`) instead of OR (`||`) to enforce "exactly one" constraint, with `BothSet` test added.
- **Unknown config type logging** — PlanValidator logs a warning for unmatched `StepConfiguration` subtypes in the switch expression default arm.
- **`BuildGraphMaps` helper** extracted to eliminate duplicate adjacency/inDegree computation across Kahn's and reachability checks.
- **35 total tests** (19 StepConfig + 16 PlanValidator), all passing.

---

## Tests (Write First)

All tests go in `src/Content/Tests/Application.Core.Tests/Validators/Planner/`. The test project already has references to `Application.Core` and `Application.AI.Common`, and has `FluentAssertions`, `Moq`, and `xunit` packages.

### PlanValidatorTests.cs

Test class that exercises the `PlanValidator` through the `IPlanValidator` interface. Each test constructs a `PlanGraph` in-memory and calls `ValidateAsync`. The validator returns `Result<PlanValidationReport>` where success includes an informational resource estimation and failure includes specific error messages.

```csharp
// File: src/Content/Tests/Application.Core.Tests/Validators/Planner/PlanValidatorTests.cs
// Namespace: Application.Core.Tests.Validators.Planner

// Test: Validate_CyclicGraph_ReturnsFail
//   Build a 3-node graph A->B->C->A. All steps are LlmCall type.
//   Assert result.IsSuccess is false.
//   Assert errors mention cycle and include the node IDs involved.

// Test: Validate_AcyclicGraph_ReturnsSuccess
//   Build a simple DAG: A->B->C (no back-edges).
//   Assert result.IsSuccess is true.

// Test: Validate_UnreachableNode_ReturnsFail
//   Build a graph with nodes in a disconnected cycle making them unreachable from
//   the acyclic portion. Example: steps [A, B, C, D, E], edges [A->B, B->C, D->E, E->D].
//   Kahn's processes A->B->C. D and E are in a cycle, not processable.
//   Validator should report both cycle AND unreachable for D, E.

// Test: Validate_ZeroRootNodes_ReturnsFail
//   Build a graph where every node has at least one incoming edge.
//   Simplest: steps [A, B], edges [A->B, B->A]. Every node has incoming.
//   Assert error message is distinct from cycle error -- mentions "no root nodes" or similar.

// Test: Validate_EdgeReferencesNonexistentStep_ReturnsFail
//   Build a graph with steps [A, B] but an edge referencing step ID "C" that doesn't exist.
//   Assert result.IsSuccess is false with referential integrity error.

// Test: Validate_ConditionalBranch_MissingTrueEdge_ReturnsFail
//   Build a graph with a ConditionalBranch step that has a ConditionalFalse outgoing edge
//   but no ConditionalTrue edge.
//   Assert failure mentions missing true branch.

// Test: Validate_ConditionalBranch_MissingFalseEdge_ReturnsFail
//   Same as above but missing the ConditionalFalse edge.

// Test: Validate_ConditionalBranch_BothEdgesPresent_ReturnsSuccess
//   ConditionalBranch step with both ConditionalTrue and ConditionalFalse edges.
//   Plus a root step feeding into the conditional. Valid DAG.
//   Assert success.

// Test: Validate_SelfReferencingSubPlan_ReturnsFail
//   Build a graph containing a SubPlanInvocation step whose SubPlanConfig.ChildPlanId
//   equals the current PlanGraph.Id.
//   Assert failure mentions self-reference.

// Test: Validate_AncestorReferencingSubPlan_ReturnsFail
//   Build a graph with ParentPlanId set (simulating a child plan).
//   One SubPlanInvocation step references the ParentPlanId as its ChildPlanId.
//   Assert failure mentions ancestor reference.

// Test: Validate_LlmCallConfig_MissingDeploymentKey_ReturnsFail
//   Build a valid graph structure with one LlmCall step whose LlmCallConfig has
//   an empty/null ModelDeploymentKey.
//   Assert failure mentions deployment key.

// Test: Validate_ToolUseConfig_UnregisteredToolKey_ReturnsFail
//   Build a valid graph with a ToolUse step referencing a tool key "nonexistent_tool".
//   Assert failure mentions unregistered tool.

// Test: Validate_HumanGateConfig_InvalidApprovalStrategy_ReturnsFail
//   Build a valid graph with a HumanGate step whose approval strategy key is invalid.
//   Assert failure mentions invalid approval strategy.

// Test: Validate_ResourceEstimation_ReturnsCriticalPathDuration
//   Build a diamond DAG: A->[B,C]->D with known timeouts on each step.
//   A=10s, B=20s, C=5s, D=10s. Critical path = A->B->D = 40s.
//   Assert the validation result includes EstimatedWallClockTime ~= 40s.

// Test: Validate_EmptyGraph_ReturnsFail
//   Build a PlanGraph with empty Steps list.
//   Assert failure mentions empty graph.

// Test: Validate_SingleStepGraph_ReturnsSuccess
//   Build a PlanGraph with one LlmCall step and no edges.
//   Assert success.
```

### StepConfigValidatorTests.cs

Tests for individual FluentValidation validators.

```csharp
// File: src/Content/Tests/Application.Core.Tests/Validators/Planner/StepConfigValidatorTests.cs
// Namespace: Application.Core.Tests.Validators.Planner

// --- LlmCallConfigValidator ---
// Test: LlmCallConfig_Valid_NoErrors
// Test: LlmCallConfig_EmptyDeploymentKey_HasError
// Test: LlmCallConfig_TemperatureOutOfRange_HasError (test both < 0 and > 2)
// Test: LlmCallConfig_MaxTokensZero_HasError

// --- ToolUseConfigValidator ---
// Test: ToolUseConfig_Valid_NoErrors
// Test: ToolUseConfig_EmptyToolName_HasError

// --- HumanGateConfigValidator ---
// Test: HumanGateConfig_Valid_NoErrors
// Test: HumanGateConfig_EmptyMessage_HasError
// Test: HumanGateConfig_InvalidStrategy_HasError
// Test: HumanGateConfig_TimeoutNegative_HasError

// --- ConditionalBranchConfigValidator ---
// Test: ConditionalBranchConfig_Valid_NoErrors
// Test: ConditionalBranchConfig_EmptyExpression_HasError
// Test: ConditionalBranchConfig_MissingTrueTarget_HasError
// Test: ConditionalBranchConfig_MissingFalseTarget_HasError

// --- SubPlanConfigValidator ---
// Test: SubPlanConfig_Valid_NoErrors
// Test: SubPlanConfig_BothNull_HasError
```

---

## Implementation Details

### PlanValidator Class

**Location**: `src/Content/Infrastructure/Infrastructure.AI/Planner/PlanValidator.cs`

**Namespace**: `Infrastructure.AI.Planner`

**Why Infrastructure, not Application?** The validator implementation depends on `IPlanStateStore` (to load parent plans for ancestor cycle detection) and may depend on service registrations to check tool key validity. The `IPlanValidator` interface lives in Application; the implementation lives in Infrastructure.

**Constructor dependencies**:
- `IEnumerable<IValidator<StepConfiguration>>` -- FluentValidation validators for step configs
- `IPlanStateStore` -- To load parent plan metadata for ancestor cycle detection
- `IServiceProvider` -- To resolve registered tool keys for `ToolUseConfig` validation
- `ILogger<PlanValidator>` -- Standard logging

**Method signature**:
```csharp
Task<Result<PlanValidationReport>> ValidateAsync(
    PlanGraph plan,
    IReadOnlySet<PlanId>? ancestorPlanIds = null,
    CancellationToken cancellationToken = default);
```

**PlanValidationReport** -- A small record returned on success:
```csharp
public record PlanValidationReport
{
    public required TimeSpan EstimatedWallClockTime { get; init; }
    public required TimeSpan EstimatedTotalComputeTime { get; init; }
    public required int CriticalPathLength { get; init; }
}
```

This record should live in `Application.AI.Common/Interfaces/Planner/` alongside `IPlanValidator`.

### Validation Check Implementations

The seven checks run in order. Early checks that fail prevent later checks from running (fail-fast for structural issues, collect all errors for config issues).

#### 1. Empty Graph Check
If `plan.Steps` is empty, return fail. No further checks.

#### 2. Edge Referential Integrity
Build a `HashSet<PlanStepId>` from `plan.Steps`. For each edge, check that `edge.From` and `edge.To` are in the set. Collect all violations. Runs before cycle detection because Kahn's algorithm assumes valid edges.

#### 3. Cycle Detection (Kahn's Algorithm)
Build adjacency list and in-degree map. Initialize queue with in-degree 0 nodes. Process nodes, decrementing successor in-degrees. If `processedCount < plan.Steps.Count`, remaining nodes are in cycles. Return failure listing unprocessed node IDs.

#### 4. Zero Root Nodes Check
If in-degree map shows no nodes with in-degree 0, return distinct error. Checked as part of Kahn's initialization.

#### 5. Unreachable Node Detection
After Kahn's succeeds, BFS from all root nodes. Safety net check -- in a valid DAG with valid referential integrity, all nodes are reachable from roots.

#### 6. Conditional Branch Completeness
For each `StepType.ConditionalBranch` step, collect outgoing edges. Check for at least one `ConditionalTrue` and one `ConditionalFalse` edge. Collect all violations.

#### 7. Self-Referencing Sub-Plan Detection
For each `SubPlanConfig` step:
- If `ChildPlanId == plan.Id`, error: self-reference
- If `ancestorPlanIds` contains `ChildPlanId`, error: ancestor reference

#### 8. Step Configuration Validation
For each step, resolve FluentValidation validator for its `StepConfiguration` subtype. Run validation. Collect all errors with step ID context.

#### 9. Resource Estimation (Informational)
If all checks pass, compute critical path using topological order from Kahn's. For each node in topological order: `longestPathTo[node] = max(longestPathTo[predecessor] + predecessor.Timeout)`. Return `PlanValidationReport`.

### FluentValidation Validators

All validators go in `src/Content/Application/Application.Core/Validation/Planner/`. Auto-discovered by `services.AddValidatorsFromAssembly(assembly)`.

#### LlmCallConfigValidator
- RuleFor ModelDeploymentKey: NotEmpty
- RuleFor Temperature: InclusiveBetween(0, 2)
- RuleFor MaxTokens: GreaterThan(0)

#### ToolUseConfigValidator
- RuleFor ToolName: NotEmpty

#### HumanGateConfigValidator
- RuleFor EscalationMessage: NotEmpty
- RuleFor ApprovalStrategy: Must be one of "AnyOf", "AllOf", "Quorum"
- RuleFor Timeout: GreaterThan(TimeSpan.Zero)

#### ConditionalBranchConfigValidator
- RuleFor ConditionExpression: NotEmpty
- RuleFor TrueEdgeTargetId: NotEmpty
- RuleFor FalseEdgeTargetId: NotEmpty

#### SubPlanConfigValidator
- Custom rule: ChildPlanId or InlinePlanDefinition must be non-null

### Key Design Decisions

1. **Fail-fast for structural issues**: Edge referential integrity and cycle detection failures prevent config validation from running.
2. **Collect all errors for config issues**: When structure is valid, all step config validators run and all errors are collected.
3. **Resource estimation is informational**: Never blocks plan creation/execution.
4. **Ancestor IDs passed explicitly**: Rather than having the validator query `IPlanStateStore` internally, ancestor plan IDs are passed as a parameter for testability.
5. **Kahn's algorithm reuse**: Topological order computed for cycle detection is reused for critical path calculation.
