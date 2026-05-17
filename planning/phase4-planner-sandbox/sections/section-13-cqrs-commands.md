# Section 13: CQRS Commands and Queries

## Overview

This section defines all MediatR commands, queries, handlers, and FluentValidation validators for the Planner subsystem. These are the public API surface through which consumers interact with plans -- generating, creating, executing, cancelling, retrying, and querying them.

All commands/queries follow the established codebase pattern: sealed record implementing `IRequest<Result<T>>`, a corresponding sealed handler class, and a sealed FluentValidation validator. Handlers return `Result<T>` for expected failures -- they never throw for business rule violations, validation errors, or not-found conditions.

## Dependencies

- **Section 01 (Domain Models)** -- All domain types: `PlanGraph`, `PlanStep`, `PlanId`, `PlanStepId`, `StepType`, `StepExecutionStatus`, `StepExecutionState`, `PlanConfiguration`, `PlanExecutionLogEntry`
- **Section 02 (Application Interfaces)** -- `IPlanExecutor`, `IPlanValidator`, `IPlanStateStore`, `IPlanGenerator`, `IPlanProgressNotifier`
- **Section 03 (Plan Validation)** -- `PlanValidator` implementation called by `CreatePlanCommandHandler` and `ExecutePlanCommandHandler`
- **Section 11 (Plan Executor)** -- `PlanExecutor` implementing `IPlanExecutor`
- **Section 12 (Plan Generator)** -- `LlmPlanGeneratorService` implementing `IPlanGenerator`

**Blocks:** Section 15 (DI Registration) -- needs these commands registered via MediatR assembly scanning.

---

## File Inventory

### Production Code

All files under `src/Content/Application/Application.Core/CQRS/Planner/`:

| File | Purpose |
|------|---------|
| `GeneratePlanCommand.cs` | Command + result DTOs for LLM-driven plan generation |
| `GeneratePlanCommandHandler.cs` | Delegates to `IPlanGenerator`, validates, persists |
| `GeneratePlanCommandValidator.cs` | FluentValidation: non-empty task description |
| `CreatePlanCommand.cs` | Command for persisting a pre-built plan graph |
| `CreatePlanCommandHandler.cs` | Validates graph, persists via `IPlanStateStore` |
| `CreatePlanCommandValidator.cs` | FluentValidation: non-null plan graph |
| `ExecutePlanCommand.cs` | Command to start or resume plan execution |
| `ExecutePlanCommandHandler.cs` | Delegates to `IPlanExecutor.ExecuteAsync` |
| `ExecutePlanCommandValidator.cs` | FluentValidation: non-empty PlanId |
| `CancelPlanCommand.cs` | Command to cancel a running plan |
| `CancelPlanCommandHandler.cs` | Delegates to `IPlanExecutor.CancelAsync` |
| `CancelPlanCommandValidator.cs` | FluentValidation: non-empty PlanId |
| `RetryPlanStepCommand.cs` | Command to retry a failed step |
| `RetryPlanStepCommandHandler.cs` | Delegates to `IPlanExecutor.RetryStepAsync` |
| `RetryPlanStepCommandValidator.cs` | FluentValidation: non-empty PlanId + StepId |
| `GetPlanQuery.cs` | Query for plan graph + current execution state |
| `GetPlanQueryHandler.cs` | Loads from `IPlanStateStore` |
| `GetPlanQueryValidator.cs` | FluentValidation: non-empty PlanId |
| `GetPlanHistoryQuery.cs` | Query for step execution audit trail |
| `GetPlanHistoryQueryHandler.cs` | Loads from `IPlanStateStore.GetExecutionHistoryAsync` |
| `GetPlanHistoryQueryValidator.cs` | FluentValidation: non-empty PlanId |
| `ListPlansQuery.cs` | Query with optional filters (status, date range) |
| `ListPlansQueryHandler.cs` | Delegates to `IPlanStateStore.ListPlansAsync` |
| `ListPlansQueryValidator.cs` | FluentValidation: date range coherence |
| `PlanExecutionSummary.cs` | DTO returned by `ExecutePlanCommand` |

Total: 25 files.

### Test Code

| File | Project | Purpose |
|------|---------|---------|
| `Application.Core.Tests/CQRS/Planner/CreatePlanCommandHandlerTests.cs` | Tests | Handler tests with mocked dependencies |
| `Application.Core.Tests/CQRS/Planner/GeneratePlanCommandHandlerTests.cs` | Tests | Handler tests with mocked generator + validator |
| `Application.Core.Tests/CQRS/Planner/ExecutePlanCommandHandlerTests.cs` | Tests | Handler tests for start and resume paths |
| `Application.Core.Tests/CQRS/Planner/CancelPlanCommandHandlerTests.cs` | Tests | Handler tests for running plan cancellation |
| `Application.Core.Tests/CQRS/Planner/RetryPlanStepCommandHandlerTests.cs` | Tests | Handler tests for failed step retry |
| `Application.Core.Tests/CQRS/Planner/PlanQueryHandlerTests.cs` | Tests | Tests for all three query handlers |
| `Application.Core.Tests/CQRS/Planner/PlanCommandValidatorTests.cs` | Tests | FluentValidation tests for all commands/queries |

---

## Tests (Write First)

### PlanCommandValidatorTests.cs

```csharp
// --- GeneratePlanCommandValidator ---

// Test: Validate_GeneratePlanCommand_ValidInput_NoErrors
// Test: Validate_GeneratePlanCommand_EmptyTaskDescription_HasError

// --- CreatePlanCommandValidator ---

// Test: Validate_CreatePlanCommand_ValidInput_NoErrors
// Test: Validate_CreatePlanCommand_NullGraph_HasError

// --- ExecutePlanCommandValidator ---

// Test: Validate_ExecutePlanCommand_ValidPlanId_NoErrors
// Test: Validate_ExecutePlanCommand_EmptyPlanId_HasError

// --- CancelPlanCommandValidator ---

// Test: Validate_CancelPlanCommand_EmptyPlanId_HasError

// --- RetryPlanStepCommandValidator ---

// Test: Validate_RetryPlanStepCommand_EmptyPlanId_HasError
// Test: Validate_RetryPlanStepCommand_EmptyStepId_HasError

// --- GetPlanQueryValidator ---

// Test: Validate_GetPlanQuery_EmptyPlanId_HasError

// --- GetPlanHistoryQueryValidator ---

// Test: Validate_GetPlanHistoryQuery_EmptyPlanId_HasError

// --- ListPlansQueryValidator ---

// Test: Validate_ListPlansQuery_ValidNoFilters_NoErrors
// Test: Validate_ListPlansQuery_FromAfterTo_HasError
```

### CreatePlanCommandHandlerTests.cs

```csharp
// Test: Handle_ValidPlan_PersistsAndReturnsPlanId
//   Mock IPlanValidator.ValidateAsync returning success.
//   Mock IPlanStateStore.SavePlanAsync returning success.
//   Assert: result.IsSuccess, IPlanStateStore.SavePlanAsync called once.

// Test: Handle_InvalidPlan_ReturnsValidationFailure
//   Mock IPlanValidator returning fail.
//   Assert: result.IsSuccess is false, IPlanStateStore.SavePlanAsync never called.
```

### GeneratePlanCommandHandlerTests.cs

```csharp
// Test: Handle_ValidTask_GeneratesAndPersistsPlan
//   Mock generator returning valid PlanGraph, validator returning success, store returning success.
//   Assert: result.IsSuccess, all three services called in order.

// Test: Handle_GeneratorFails_ReturnsFailResult
//   Mock generator returning fail.
//   Assert: result.IsSuccess is false, validator/store never called.

// Test: Handle_GeneratedPlanFailsValidation_ReturnsValidationFailure
//   Mock generator returns plan, validator returns fail.
//   Assert: result.IsSuccess is false, store never called.
```

### ExecutePlanCommandHandlerTests.cs

```csharp
// Test: Handle_NewPlan_StartsExecution
//   Mock store returning valid graph, validator returning success, executor returning success.
//   Assert: result.IsSuccess, executor.ExecuteAsync called once.

// Test: Handle_ExistingPlan_ResumesFromCheckpoint
//   Mock store returning plan with some completed steps.
//   Assert: result.IsSuccess, executor.ExecuteAsync called (resume is internal to executor).

// Test: Handle_PlanNotFound_ReturnsNotFound
//   Mock store returning null.
//   Assert: result.IsSuccess is false, result.FailureType is NotFound.

// Test: Handle_PlanFailsValidation_ReturnsValidationFailure
//   Mock store returns plan, validator returns fail.
//   Assert: result is validation failure, executor never called.
```

### CancelPlanCommandHandlerTests.cs

```csharp
// Test: Handle_RunningPlan_MarksStepsAsSkipped
// Test: Handle_NonexistentPlan_ReturnsNotFound
```

### RetryPlanStepCommandHandlerTests.cs

```csharp
// Test: Handle_FailedStep_RestartsStep
// Test: Handle_NonFailedStep_ReturnsFail
```

### PlanQueryHandlerTests.cs

```csharp
// --- GetPlanQueryHandler ---
// Test: Handle_ExistingPlan_ReturnsGraphAndState
// Test: Handle_NonexistentPlan_ReturnsNotFound

// --- GetPlanHistoryQueryHandler ---
// Test: Handle_ExecutedPlan_ReturnsAuditTrail

// --- ListPlansQueryHandler ---
// Test: Handle_WithFilters_ReturnsMatchingPlans
```

---

## Implementation Details

### Namespace and Project

All production code in `src/Content/Application/Application.Core/CQRS/Planner/`.

### Commands

#### GeneratePlanCommand

```csharp
public sealed record GeneratePlanCommand : IRequest<Result<PlanId>>
{
    public required string TaskDescription { get; init; }
    public PlanGenerationConstraints? Constraints { get; init; }
}
```

**Handler flow:** IPlanGenerator.GenerateAsync -> IPlanValidator.ValidateAsync -> IPlanStateStore.SavePlanAsync -> return PlanId

#### CreatePlanCommand

```csharp
public sealed record CreatePlanCommand : IRequest<Result<PlanId>>
{
    public required PlanGraph Plan { get; init; }
}
```

**Handler flow:** IPlanValidator.ValidateAsync -> IPlanStateStore.SavePlanAsync -> return PlanId

#### ExecutePlanCommand

```csharp
public sealed record ExecutePlanCommand : IRequest<Result<PlanExecutionSummary>>
{
    public required PlanId PlanId { get; init; }
}
```

**Handler flow:** IPlanStateStore.LoadPlanAsync (NotFound if null) -> IPlanValidator.ValidateAsync -> IPlanExecutor.ExecuteAsync -> return summary

#### CancelPlanCommand

```csharp
public sealed record CancelPlanCommand : IRequest<Result>
{
    public required PlanId PlanId { get; init; }
}
```

**Handler flow:** IPlanExecutor.CancelAsync -> return result

#### RetryPlanStepCommand

```csharp
public sealed record RetryPlanStepCommand : IRequest<Result>
{
    public required PlanId PlanId { get; init; }
    public required PlanStepId StepId { get; init; }
}
```

**Handler flow:** IPlanExecutor.RetryStepAsync -> return result

### Queries

#### GetPlanQuery

```csharp
public sealed record GetPlanQuery : IRequest<Result<PlanSnapshot>>
{
    public required PlanId PlanId { get; init; }
}
```

Returns `PlanSnapshot`:

```csharp
public sealed record PlanSnapshot
{
    public required PlanGraph Graph { get; init; }
    public required IReadOnlyDictionary<PlanStepId, StepExecutionState> StepStates { get; init; }
}
```

#### GetPlanHistoryQuery

```csharp
public sealed record GetPlanHistoryQuery : IRequest<Result<IReadOnlyList<PlanExecutionLogEntry>>>
{
    public required PlanId PlanId { get; init; }
}
```

#### ListPlansQuery

```csharp
public sealed record ListPlansQuery : IRequest<Result<IReadOnlyList<PlanGraph>>>
{
    public StepExecutionStatus? StatusFilter { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
}
```

### Result DTO: PlanExecutionSummary

```csharp
public sealed record PlanExecutionSummary
{
    public required PlanId PlanId { get; init; }
    public required bool Success { get; init; }
    public required TimeSpan Duration { get; init; }
    public required IReadOnlyDictionary<StepExecutionStatus, int> StepStatusCounts { get; init; }
    public PlanStepId? FailedStepId { get; init; }
    public string? ErrorMessage { get; init; }
    public string? FinalOutput { get; init; }
}
```

Note: `PlanExecutionSummary` is referenced by `IPlanExecutor` in Section 02. Since `IPlanExecutor` lives in `Application.AI.Common`, this type may need to live there too to avoid circular references. The implementer should check where `IPlanExecutor` is defined and place `PlanExecutionSummary` in the same project.

### FluentValidation Validators

Each validator is a `sealed class` extending `AbstractValidator<T>`:
- **GeneratePlanCommandValidator**: `TaskDescription` NotEmpty
- **CreatePlanCommandValidator**: `Plan` NotNull
- **ExecutePlanCommandValidator**: `PlanId` must not wrap `Guid.Empty`
- **CancelPlanCommandValidator**: `PlanId` non-empty GUID
- **RetryPlanStepCommandValidator**: `PlanId` + `StepId` both non-empty GUIDs
- **GetPlanQueryValidator**: `PlanId` non-empty GUID
- **GetPlanHistoryQueryValidator**: `PlanId` non-empty GUID
- **ListPlansQueryValidator**: `To >= From` when both non-null

### Registration

These commands and handlers are auto-discovered by MediatR's assembly scanning. The existing `DependencyInjection.cs` in `Application.Core` already calls `services.AddMediatR(...)`. Validators are auto-discovered by FluentValidation's assembly scanning. No manual registration is needed for this section.

### Testing Strategy

- **Unit tests only** -- mock all interface dependencies
- **Moq** with `ReturnsAsync` for happy path
- **xUnit Assert** for assertions (FluentAssertions not used in this project)
- **FluentValidation.TestHelper** for validator tests (namespace within FluentValidation package)
- **Test naming:** `MethodName_Scenario_ExpectedResult`
- **Test helper:** `CreateValidPlanGraph()` for minimal valid graph construction

---

## Implementation Notes (Post-Build)

### Deviations from Plan

1. **PlanSnapshot is a class, not a record** — `PlanSnapshot` was implemented as a `sealed class` (not record) because it's a mutable DTO used within the Result<T> pattern, consistent with how other DTOs in this layer work.

2. **PlanExecutionSummary already existed** — The `PlanExecutionSummary` type was already defined in `Application.AI.Common` (Section 02) alongside `IPlanExecutor`. No new file was created for it.

3. **Commands are classes, not records** — All commands/queries use `sealed class` with `{ get; init; }` properties, matching the existing codebase CQRS pattern rather than the spec's `sealed record` suggestion.

4. **No FluentAssertions dependency** — Tests use xUnit's built-in `Assert` class. The project does not reference FluentAssertions.

5. **FluentValidation.TestHelper** — Not a separate NuGet package in v11.x; it's a namespace within the main `FluentValidation` package. Added `FluentValidation` package reference directly to the test project.

### Code Review Fixes Applied

1. **Added `LoadStepStatesAsync` to `IPlanStateStore`** — `GetPlanQueryHandler` was using `ResumeAsync` (which resets Running→Ready) for a read-only query. Added a dedicated read-only method `LoadStepStatesAsync` to the interface and `EfCorePlanStateStore` implementation. Uses `AsNoTracking()` and returns empty dictionary (not NotFound) when no states exist.

2. **Added `MaximumLength(10_000)` to `GeneratePlanCommandValidator`** — TaskDescription had no upper-bound length constraint.

3. **Removed unused `_logger` fields** — `GetPlanHistoryQueryHandler` and `ListPlansQueryHandler` injected `ILogger<T>` but never used it. Removed the fields, constructor parameters, and `using Microsoft.Extensions.Logging;` imports.

### Files Modified Outside Section 13

- `src/Content/Application/Application.AI.Common/Interfaces/Planner/IPlanStateStore.cs` — Added `LoadStepStatesAsync` method
- `src/Content/Infrastructure/Infrastructure.AI/Planner/EfCorePlanStateStore.cs` — Added `LoadStepStatesAsync` implementation

### Test Summary

- **38 tests total**, all passing
- 15 validator tests, 14 handler tests, 4 query handler tests, 5 additional handler edge cases
