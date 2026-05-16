# Section 5: Plan State Store

## Overview

This section implements `EfCorePlanStateStore`, the concrete `IPlanStateStore` that bridges the planner's domain operations (save, load, update, checkpoint, resume) to the EF Core persistence layer built in Section 04. It uses `IDbContextFactory<PlannerDbContext>` to create short-lived `DbContext` instances, making it safe for use from both scoped and singleton callers. Optimistic concurrency is handled via RowVersion tokens on the underlying entities.

## Dependencies

- **Section 01 (Domain Models)**: `PlanGraph`, `PlanStep`, `PlanEdge`, `PlanId`, `PlanStepId`, `StepExecutionState`, `StepExecutionStatus`, `PlanConfiguration`, `StepConfiguration` (polymorphic), `RetryPolicy`, `EdgeType`, `StepType`
- **Section 02 (Application Interfaces)**: `IPlanStateStore` interface definition
- **Section 04 (EF Core Persistence)**: `PlannerDbContext`, all entity types, entity configurations with RowVersion concurrency tokens, JSON column handling

## File Locations

### Implementation

- `src/Content/Infrastructure/Infrastructure.AI/Planner/EfCorePlanStateStore.cs`

### Tests

- `src/Content/Tests/Infrastructure.AI.Tests/Planner/EfCorePlanStateStoreTests.cs`

## Tests (Write First)

All tests use in-memory SQLite. Each test creates a fresh database. Concurrency tests use file-based SQLite.

```csharp
namespace Infrastructure.AI.Tests.Planner;

public sealed class EfCorePlanStateStoreTests : IDisposable
{
    // Test: SavePlanAsync_NewPlan_PersistsGraphAndSteps
    //   Arrange: Create a PlanGraph with 3 steps and 2 edges.
    //   Act: Call SavePlanAsync.
    //   Assert: Verify PlanGraphEntity, 3 PlanStepEntity rows, 2 PlanEdgeEntity rows,
    //     and 3 StepExecutionStateEntity rows initialized to Pending.

    // Test: LoadPlanAsync_ExistingPlan_ReturnsCompleteGraph
    //   Arrange: SavePlanAsync a graph, then create fresh store instance.
    //   Act: Call LoadPlanAsync.
    //   Assert: Returned PlanGraph matches. Verify polymorphic StepConfiguration round-trip.

    // Test: LoadPlanAsync_NonexistentPlan_ReturnsNull
    //   Act: Call LoadPlanAsync with random PlanId.
    //   Assert: Returns null.

    // Test: UpdateStepStateAsync_StatusTransition_PersistsNewState
    //   Arrange: Save a plan.
    //   Act: Transition Pending -> Running -> Completed.
    //   Assert: Each state persisted correctly with timestamps and output.

    // Test: UpdateStepStateAsync_ConcurrentUpdate_HandlesOptimisticConcurrency
    //   Arrange: Save plan to file-based SQLite. Load step in two contexts.
    //   Act: Update in both, save first, then second.
    //   Assert: Second throws DbUpdateConcurrencyException.

    // Test: GetExecutionHistoryAsync_MultipleSteps_ReturnsChronological
    //   Arrange: Save plan, transition multiple steps.
    //   Act: Call GetExecutionHistoryAsync.
    //   Assert: Entries ordered by Timestamp ascending.

    // Test: CheckpointAsync_MidExecution_SavesAllStepStates
    //   Arrange: Save plan with 4 steps. Set various states.
    //   Act: Call CheckpointAsync.
    //   Assert: All states persisted. Checkpoint log entry appended.

    // Test: ResumeAsync_FromCheckpoint_RebuildsReadyQueue
    //   Arrange: Save plan (A->B->C, D independent). Set states via DB manipulation.
    //   Act: Call ResumeAsync.
    //   Assert: Returns ready step IDs. Running steps transitioned back to Ready.
}
```

## Implementation Details

### `EfCorePlanStateStore` Class

**Constructor dependencies**:
- `IDbContextFactory<PlannerDbContext>` -- creates short-lived contexts per operation
- `ILogger<EfCorePlanStateStore>`
- `TimeProvider` -- for testable timestamps

**Interface contract** (`IPlanStateStore`):
```csharp
public interface IPlanStateStore
{
    Task SavePlanAsync(PlanGraph plan, CancellationToken cancellationToken = default);
    Task<PlanGraph?> LoadPlanAsync(PlanId planId, CancellationToken cancellationToken = default);
    Task UpdateStepStateAsync(PlanId planId, StepExecutionState state, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PlanExecutionLogEntry>> GetExecutionHistoryAsync(PlanId planId, CancellationToken cancellationToken = default);
    Task CheckpointAsync(PlanId planId, IReadOnlyList<StepExecutionState> stepStates, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PlanStepId>> ResumeAsync(PlanId planId, CancellationToken cancellationToken = default);
}
```

### Method-by-Method Design

#### `SavePlanAsync`
Map domain model to entities. Add all entities. Append "plan_created" log. SaveChangesAsync.

#### `LoadPlanAsync`
Query with `.Include()` for eager loading. Return null if not found. Map entities back to domain models with polymorphic StepConfiguration deserialization.

#### `UpdateStepStateAsync`
Load entity, update fields, increment Version, append transition log. Catches `DbUpdateConcurrencyException` and returns `Result.Fail` -- consistent with Result<T> contract. Caller retries via Result.IsSuccess check.

#### `GetExecutionHistoryAsync`
Query `PlanExecutionLogEntity` ordered by auto-increment `Id` (not `Timestamp`, due to SQLite DateTimeOffset ORDER BY limitation). Filters to step-level events only (StepId != null, EventType parseable as StepExecutionStatus).

#### `CheckpointAsync`
Validates all step IDs exist in plan (returns `Result.Fail` if any missing). Bulk update all step states in a single `SaveChangesAsync`. Append "checkpoint" log entry. Returns `Result.NotFound` for empty plan.

#### `ResumeAsync`
Load step states for plan. Transition `Running` steps back to `Ready` (with Version increment). Append "resumed" log. Returns `IReadOnlyDictionary<PlanStepId, StepExecutionState>` with all step states.

#### `ListPlansAsync`
Queries with date range filters server-side. Status filter applied client-side (SQLite limitation). Capped at 100 results. Full pagination deferred to CQRS commands (section 13).

### Design Decisions

1. **IDbContextFactory over injected DbContext**: Creates fresh contexts per operation, safe for singleton callers.
2. **Concurrency via Result.Fail**: Catches `DbUpdateConcurrencyException` and returns failure Result. Caller decides retry strategy.
3. **Manual Version increment**: SQLite lacks native rowversion; code increments `entity.Version++` before SaveChanges.
4. **Append-only execution log**: Insert-only for complete audit trail. Plan-level events (plan_created, checkpoint, resumed) stored with null StepId.
5. **Private mapping methods**: No AutoMapper -- simple enough for dedicated methods. Steps ordered by Name on load for deterministic ordering.
6. **TimeProvider for timestamps**: Enables deterministic testing via FakeTimeProvider.
7. **OrderBy Id for logs**: Auto-increment long guarantees chronological order on SQLite without DateTimeOffset ORDER BY.

### Actual Files Created

- `src/Content/Infrastructure/Infrastructure.AI/Planner/EfCorePlanStateStore.cs` (400 lines)
- `src/Content/Tests/Infrastructure.AI.Tests/Planner/EfCorePlanStateStoreTests.cs` (420 lines, 10 tests)

### Registration

```csharp
services.AddScoped<IPlanStateStore, EfCorePlanStateStore>();
```
