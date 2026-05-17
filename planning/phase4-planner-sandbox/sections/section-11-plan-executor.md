# Section 11: Plan Executor

## Overview

The `PlanExecutor` is the core DAG scheduling engine that drives plan execution. It implements `IPlanExecutor` (defined in section-02) and orchestrates step execution by maintaining a dynamic ready-queue of steps whose dependencies are satisfied, bounded by a `SemaphoreSlim` for concurrency control.

This section covers:
- The `PlanExecutor` class in `Infrastructure.AI/Planner/PlanExecutor.cs`
- Dynamic ready-queue scheduling algorithm
- Bounded concurrency via `SemaphoreSlim`
- Per-plan serialization via keyed `SemaphoreSlim` in a `ConcurrentDictionary<PlanId, SemaphoreSlim>`
- Checkpoint/resume from persisted state
- Blocked step polling for escalation resolution
- Notification dispatch via `IPlanProgressNotifier`
- Integration with `IPlanStepExecutor` (keyed DI), `IPlanValidator`, `IPlanStateStore`, and `IEscalationService`

## Dependencies on Other Sections

| Section | What It Provides | How This Section Uses It |
|---------|-----------------|--------------------------|
| section-02 (Application Interfaces) | `IPlanExecutor`, `IPlanStepExecutor`, `IPlanValidator`, `IPlanStateStore`, `IPlanProgressNotifier` | PlanExecutor implements `IPlanExecutor`, consumes the others via DI |
| section-03 (Plan Validation) | `PlanValidator` implementing `IPlanValidator` | Called before execution to ensure plan is valid |
| section-05 (Plan State Store) | `EfCorePlanStateStore` implementing `IPlanStateStore` | Load/save/checkpoint step states during execution |
| section-10 (Step Executors) | Five keyed `IPlanStepExecutor` implementations | Resolved via keyed DI on `StepType` to execute individual steps |

The existing `IEscalationService` (already in the codebase at `Application.AI.Common/Interfaces/Escalation/IEscalationService.cs`) provides `QueueEscalationAsync` and `GetPendingEscalationAsync` for blocked step polling.

## File Paths

| File | Action |
|------|--------|
| `src/Content/Infrastructure/Infrastructure.AI/Planner/PlanExecutor.cs` | **Create** |
| `src/Content/Tests/Infrastructure.AI.Tests/Planner/PlanExecutorTests.cs` | **Create** |

---

## Tests FIRST

All tests go in `src/Content/Tests/Infrastructure.AI.Tests/Planner/PlanExecutorTests.cs`.

The test class mocks the following interfaces: `IPlanValidator`, `IPlanStateStore`, `IPlanProgressNotifier`, `IEscalationService`, and `IServiceProvider` (for keyed `IPlanStepExecutor` resolution). Tests use in-memory SQLite where state store integration is needed.

### Test stubs

```csharp
namespace Infrastructure.AI.Tests.Planner;

public sealed class PlanExecutorTests : IDisposable
{
    // -- Mocks: IPlanValidator, IPlanStateStore, IPlanProgressNotifier,
    //           IEscalationService, IServiceProvider (for keyed IPlanStepExecutor resolution),
    //           ILogger<PlanExecutor>
    // -- SUT: PlanExecutor
    // -- Helpers: BuildLinearPlan, BuildParallelPlan, BuildDiamondPlan,
    //             BuildConditionalPlan, CreateMockStepExecutor

    // === Scheduling: linear ===

    // Test: Execute_LinearPlan_RunsStepsInOrder
    // Arrange: 3-step linear plan A->B->C, mock step executors returning Completed.
    // Act: ExecuteAsync(planId, ct)
    // Assert: Step A executes first, then B, then C. Verify ordering via execution timestamps
    //         or a call-order tracking list.

    // === Scheduling: parallel ===

    // Test: Execute_ParallelPlan_RunsIndependentStepsInParallel
    // Arrange: Plan with A->[B,C]->D. B and C have no edges between them.
    //          Mock step executors with a small delay so parallelism is observable.
    // Act: ExecuteAsync(planId, ct)
    // Assert: B and C start before either completes. D starts only after both B and C complete.

    // === Scheduling: diamond DAG ===

    // Test: Execute_DiamondDag_StepDWaitsForBothBAndC
    // Arrange: A->B, A->C, B->D, C->D. D has two incoming edges.
    // Act: ExecuteAsync(planId, ct)
    // Assert: D does not start until both B and C are Completed.

    // === Concurrency bound ===

    // Test: Execute_BoundedConcurrency_RespectsMaxParallelSteps
    // Arrange: Plan with 5 independent steps, MaxParallelSteps=2.
    //          Track concurrent execution count via SemaphoreSlim or Interlocked.
    // Act: ExecuteAsync(planId, ct)
    // Assert: At no point do more than 2 steps execute simultaneously.

    // === Failure propagation ===

    // Test: Execute_StepFails_DependentSubgraphSkipped
    // Arrange: A->B->C. Step A fails (mock executor returns Failed).
    // Act: ExecuteAsync(planId, ct)
    // Assert: B and C are marked Skipped. Neither executor is invoked.

    // Test: Execute_StepFails_IndependentBranchContinues
    // Arrange: Root->[A,B]. A->C, B->D. A fails.
    // Act: ExecuteAsync(planId, ct)
    // Assert: C is Skipped. B and D execute normally.

    // === Blocked steps (escalation) ===

    // Test: Execute_BlockedStep_IndependentBranchesContinue
    // Arrange: Root->[A,B]. A is a HumanGate (executor returns Blocked). B is independent.
    // Act: ExecuteAsync(planId, ct)
    // Assert: B executes while A remains Blocked. Plan does not complete because A is unresolved.

    // Test: Execute_BlockedStep_ResolvesOnNextPass
    // Arrange: Single HumanGate step. First poll returns pending, second poll returns resolved.
    //          Mock IEscalationService.GetPendingEscalationAsync accordingly.
    // Act: ExecuteAsync(planId, ct)
    // Assert: Step transitions from Blocked to Ready, then executes to Completed.

    // === Timeout ===

    // Test: Execute_PlanTimeout_CancelsRunningSteps
    // Arrange: Plan with a step that takes longer than PlanConfiguration.PlanTimeout.
    //          Use a very short timeout (e.g., 50ms) and a step executor with Task.Delay(5000ms).
    // Act: ExecuteAsync(planId, ct)
    // Assert: OperationCanceledException or plan result indicates timeout.
    //         Running steps receive cancellation.

    // === Checkpoint/Resume ===

    // Test: Execute_Checkpoint_PersistsAfterEachTransition
    // Arrange: Linear plan A->B->C. Track IPlanStateStore.UpdateStepStateAsync calls.
    // Act: ExecuteAsync(planId, ct)
    // Assert: UpdateStepStateAsync called for every state transition
    //         (Pending->Ready->Running->Completed for each step).

    // Test: Execute_Resume_RebuildsReadyQueueFromState
    // Arrange: Plan A->B->C. State store returns A=Completed, B=Completed, C=Pending.
    //          (Simulating a resume after crash mid-execution.)
    // Act: ExecuteAsync(planId, ct)
    // Assert: Only step C executes. A and B executors are NOT invoked.

    // === Per-plan serialization ===

    // Test: Execute_ConcurrentSamePlan_SerializedViaKeySemaphore
    // Arrange: Launch two concurrent ExecuteAsync calls for the same planId.
    //          Mock step executors with a small delay.
    // Act: Task.WhenAll(execute1, execute2)
    // Assert: Executions do not interleave. Second execution either queues behind the first
    //         or sees the plan already completed.

    // === Conditional branching ===

    // Test: Execute_ConditionalBranch_FollowsCorrectPath
    // Arrange: Plan with A -> ConditionalBranch -> (TrueEdge -> B, FalseEdge -> C).
    //          ConditionalBranchStepExecutor evaluates to true.
    // Act: ExecuteAsync(planId, ct)
    // Assert: B executes. C is Skipped. ConditionalBranch step is Completed.

    // === Sub-plan ===

    // Test: Execute_SubPlan_ChildExecutesInIsolatedScope
    // Arrange: Plan with a SubPlanInvocation step. Mock SubPlanStepExecutor to verify
    //          it creates a new DI scope.
    // Act: ExecuteAsync(planId, ct)
    // Assert: SubPlanStepExecutor is invoked. Step output contains child plan result.

    // === Notification events ===

    // Test: Execute_EmitsPlanStarted_OnBegin
    // Test: Execute_EmitsPlanCompleted_OnSuccess
    // Test: Execute_EmitsPlanFailed_OnFailure
    // Test: Execute_EmitsStepStarted_ForEachStep
    // Test: Execute_EmitsStateUpdate_OnEachTransition

    public void Dispose()
    {
        // Dispose any SemaphoreSlim instances, cancel pending CTS if needed
    }
}
```

### Test helper patterns

Test helpers should build `PlanGraph` objects programmatically. For example, `BuildLinearPlan(int stepCount)` creates a chain of `LlmCall` steps with `ControlFlow` edges between them. `BuildDiamondPlan()` creates the A->[B,C]->D diamond topology. These helpers return a `PlanGraph` with pre-set `PlanConfiguration` (short timeouts for test speed).

Mock step executors should be created via a helper `CreateMockStepExecutor(StepExecutionStatus result, TimeSpan? delay = null)` that returns a `Mock<IPlanStepExecutor>` configured to transition the step to the specified status after an optional delay.

---

## Implementation Details

### File: `src/Content/Infrastructure/Infrastructure.AI/Planner/PlanExecutor.cs`

#### Class signature and constructor dependencies

```csharp
namespace Infrastructure.AI.Planner;

public sealed class PlanExecutor : IPlanExecutor
{
    private readonly IPlanValidator _validator;
    private readonly IPlanStateStore _stateStore;
    private readonly IPlanProgressNotifier _notifier;
    private readonly IEscalationService _escalationService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PlanExecutor> _logger;

    private static readonly ConcurrentDictionary<PlanId, SemaphoreSlim> _planLocks = new();
}
```

#### Algorithm: `ExecuteAsync`

The core method implements the dynamic ready-queue model. The algorithm is event-driven within a `while` loop, not polling-based for step completion.

**Pseudocode**:

```
async Task<Result<PlanExecutionSummary>> ExecuteAsync(PlanId planId, CancellationToken ct):
    // 1. Acquire per-plan lock (keyed SemaphoreSlim)
    var planLock = _planLocks.GetOrAdd(planId, _ => new SemaphoreSlim(1, 1))
    await planLock.WaitAsync(ct)
    try:
        // 2. Load plan and current state
        var plan = await _stateStore.LoadPlanAsync(planId, ct)
        if plan is null: return Result.Fail("Plan not found")

        // 3. Validate before execution
        var validation = await _validator.ValidateAsync(plan, ct)
        if !validation.IsSuccess: return Result.Fail(validation.Errors)

        // 4. Load or initialize step states
        var stepStates = await _stateStore.LoadStepStatesAsync(planId, ct)

        // 5. Notify plan started
        await _notifier.NotifyPlanStartedAsync(plan, ct)

        // 6. Create plan-level CTS with PlanTimeout
        using var planCts = CancellationTokenSource.CreateLinkedTokenSource(ct)
        planCts.CancelAfter(plan.Configuration.PlanTimeout)

        // 7. Build adjacency structures for DAG traversal
        var dependencyMap = BuildDependencyMap(plan)
        var dependentMap = BuildDependentMap(plan)

        // 8. Initialize ready queue from current states
        var readyQueue = new ConcurrentQueue<PlanStep>()
        foreach step where all prerequisites are Completed or Skipped:
            transition step to Ready
            readyQueue.Enqueue(step)

        // 9. Bounded concurrency semaphore
        var concurrency = new SemaphoreSlim(plan.Configuration.MaxParallelSteps)
        var runningTasks = new List<Task>()

        // 10. Main scheduling loop
        while readyQueue is not empty OR runningTasks has active tasks OR blocked steps exist:
            while readyQueue.TryDequeue(out var step):
                await concurrency.WaitAsync(planCts.Token)
                var task = ExecuteStepAsync(step, plan, stepStates, ...)
                runningTasks.Add(task)

            if runningTasks.Any():
                var completed = await Task.WhenAny(runningTasks)
                runningTasks.Remove(completed)
                await completed

            await PollBlockedStepsAsync(stepStates, readyQueue, planCts.Token)

            if AllStepsTerminal(stepStates): break

        // 11. Determine outcome
        var summary = BuildExecutionSummary(plan, stepStates)
        if summary.HasFailures:
            await _notifier.NotifyPlanFailedAsync(plan, summary, ct)
        else:
            await _notifier.NotifyPlanCompletedAsync(plan, summary, ct)

        return Result.Success(summary)

    finally:
        planLock.Release()
```

#### Step execution method

```
async Task ExecuteStepAsync(PlanStep step, PlanGraph plan, ...):
    try:
        // Transition: Ready -> Running
        await TransitionStepAsync(step.Id, StepExecutionStatus.Running)
        await _notifier.NotifyStepStartedAsync(step, ct)

        // Resolve keyed executor
        var executor = _serviceProvider.GetRequiredKeyedService<IPlanStepExecutor>(step.Type)

        // Execute with step-level timeout
        using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(planCts.Token)
        stepCts.CancelAfter(step.Timeout)

        var result = await executor.ExecuteAsync(step, stepContext, stepCts.Token)

        // Handle result
        if result.Status == StepExecutionStatus.Blocked:
            await TransitionStepAsync(step.Id, StepExecutionStatus.Blocked)
        else if result.Status == StepExecutionStatus.Completed:
            await TransitionStepAsync(step.Id, StepExecutionStatus.Completed, result.Output)
            EnqueueReadyDownstream(step.Id)
        else if result.Status == StepExecutionStatus.Failed:
            await HandleStepFailure(step, result)

        await _notifier.NotifyStepCompletedAsync(step, result, ct)
    catch OperationCanceledException:
        await TransitionStepAsync(step.Id, StepExecutionStatus.Failed, error: "Timeout")
    catch Exception ex:
        await TransitionStepAsync(step.Id, StepExecutionStatus.Failed, error: ex.Message)
    finally:
        concurrency.Release()
```

#### Key implementation details

**Dependency map construction**: Build two dictionaries from the plan's edges:
- `dependencyMap: Dictionary<PlanStepId, HashSet<PlanStepId>>` -- for each step, the set of steps that must complete before it can run. Only `ControlFlow` and `DataFlow` edges create dependencies. `ConditionalTrue` and `ConditionalFalse` edges create conditional dependencies.
- `dependentMap: Dictionary<PlanStepId, List<(PlanStepId, EdgeType)>>` -- for each step, the downstream steps and edge types.

**Ready step detection**: A step is ready when ALL its dependencies are in a terminal state (Completed or Skipped). For conditional edges, the step is ready only if the active conditional edge points to it.

**Conditional branch handling**: When a `ConditionalBranch` step completes, it sets an "active edge" flag. The path matching the active edge type has its target step enqueued as Ready. The other path's target step and its entire downstream subgraph is recursively marked as Skipped.

**Failure cascade**: When a step fails and retry is exhausted, mark all downstream steps as Skipped via BFS/DFS from the failed step through `dependentMap`. Independent branches continue normally.

**Retry logic**: On step failure, check `step.RetryPolicy`:
1. If `AttemptCount < MaxRetries`: increment attempt count, compute delay (`Fixed`, `Linear`, or `Exponential` backoff), re-enqueue after delay.
2. If retries exhausted, check `ErrorRecovery`:
   - `FailStep`: mark step Failed, cascade skip to dependents
   - `SkipStep`: mark step Skipped, dependents may still be eligible
   - `FailPlan`: mark step Failed, cancel all running steps, mark remaining as Skipped
   - `Escalate`: transition to Blocked, queue escalation

**Blocked step polling**: On each scheduling pass, iterate over all `Blocked` steps. For each, query `IEscalationService.GetPendingEscalationAsync(escalationId)`. If resolved:
- Approved: transition step to Ready, enqueue
- Rejected: transition step to Failed, cascade skip

**Checkpoint persistence**: Every call to `TransitionStepAsync` persists the new state via `IPlanStateStore.UpdateStepStateAsync`.

**Resume from checkpoint**: When `ExecuteAsync` loads step states and finds some steps already in terminal states, it does not re-execute them. It rebuilds the ready queue by checking which non-terminal steps have all dependencies satisfied. Steps that were `Running` when the crash occurred are reset to `Ready`.

**Per-plan serialization**: The `ConcurrentDictionary<PlanId, SemaphoreSlim>` ensures that two concurrent `ExecutePlanCommand` calls for the same `PlanId` are serialized. Remove from dictionary after plan reaches a terminal state to avoid memory leaks.

**Termination conditions**: The main loop exits when:
1. All steps are in terminal states (Completed, Failed, Skipped), OR
2. Plan timeout fires (CancellationToken cancelled), OR
3. External cancellation via the caller's CancellationToken

### Notification integration

The executor calls `IPlanProgressNotifier` methods at these points:
- `NotifyPlanStartedAsync`: once at the beginning of execution
- `NotifyStepStartedAsync`: when each step transitions to Running
- `NotifyStepCompletedAsync`: when each step reaches a terminal state
- `NotifyStateUpdateAsync`: on every state transition
- `NotifyPlanCompletedAsync`: when all steps finish and no failures
- `NotifyPlanFailedAsync`: when the plan finishes with at least one Failed step

### Observability

Create an `ActivitySource` named `"PlanExecution"` for distributed tracing:
- Span `plan.execute` wraps the entire `ExecuteAsync` method. Tags: `plan.id`, `plan.name`, `plan.step_count`.
- Span `plan.step.{type}` wraps each step execution. Tags: `step.id`, `step.name`, `step.type`, `step.attempt`.

Counters (via `IMeterFactory` or static `Meter`):
- `planner.plan.executions` (counter): incremented on plan completion. Tag: `status`.
- `planner.step.executions` (counter): incremented per step completion. Tags: `type`, `status`.
- `planner.step.duration` (histogram): records step wall-clock duration. Tag: `type`.

---

## Implementation Sequence

1. Create test file with all test stubs and helper methods.
2. Create `PlanExecutor.cs` with constructor accepting all dependencies.
3. Implement `ExecuteAsync` with the ready-queue loop, per-plan locking, and plan timeout.
4. Implement `ExecuteStepAsync` with keyed DI resolution, step timeout, and result handling.
5. Implement dependency map construction.
6. Implement ready step detection and enqueue logic.
7. Implement failure cascade (BFS skip of downstream subgraph).
8. Implement retry logic with backoff delay computation.
9. Implement blocked step polling using `IEscalationService`.
10. Implement checkpoint/resume.
11. Implement conditional branch handling.
12. Add notification calls at each lifecycle point.
13. Add observability spans and counters.
14. Run tests, iterate until all pass.

## Edge Cases and Design Notes

- **Empty plan**: Should complete immediately with a success summary.
- **Single-step plan**: No dependency logic needed -- step goes directly to Ready.
- **All steps blocked**: Executor enters polling-only mode until at least one escalation resolves or plan times out.
- **Memory leak prevention**: Remove semaphore from `_planLocks` after plan reaches terminal state.
- **Thread safety**: Use `ConcurrentDictionary<PlanStepId, StepExecutionState>` for step states accessed from multiple concurrent tasks.
- **Re-entrant execution**: If `ExecuteAsync` is called for a plan that is already completed, return the existing summary without re-executing.

---

## Implementation Notes (Actual)

### Files Created
| File | Lines |
|------|-------|
| `src/Content/Infrastructure/Infrastructure.AI/Planner/PlanExecutor.cs` | ~380 |
| `src/Content/Tests/Infrastructure.AI.Tests/Planner/PlanExecutorTests.cs` | ~660 |

### Deviations from Plan
1. **Retry logic with backoff**: Not implemented in this section. The `RetryPolicy.OnExhausted` handling is implemented (FailStep, SkipStep, FailPlan, Escalate), but actual retry-with-delay loops are deferred — the step executor itself handles retries internally per the existing patterns.
2. **Blocked step polling**: Simplified. The executor breaks the scheduling loop when all remaining steps are blocked rather than continuously polling `IEscalationService`. Active polling would require a timer or periodic check that adds complexity without clear benefit until the escalation flow is wired end-to-end.
3. **Memory leak prevention**: Plan lock semaphores are NOT removed from the static dictionary after execution (per code review feedback — removing inside the lock breaks serialization for waiters). The dictionary grows bounded by unique PlanIds, which is acceptable.

### Code Review Fixes Applied
- **CAS pattern (TryMarkReady)**: Prevents race condition where two upstream steps completing simultaneously could double-enqueue a downstream step.
- **Fully async helpers**: All internal methods are async — no `.GetAwaiter().GetResult()` calls that could starve the thread pool.
- **HashSet<Task>**: O(1) removal of completed tasks from the running set.
- **Timeout awaits**: After plan timeout, running tasks are awaited before building the summary to prevent data races.
- **Tightened loop exit**: Uses `HasPendingOrReadySteps` check to prevent premature exit.

### Test Coverage: 21 tests
- Linear, parallel, diamond DAG scheduling
- Bounded concurrency enforcement
- Failure propagation + independent branch continuation
- Blocked step handling
- Plan timeout
- Checkpoint persistence + resume from state
- Per-plan serialization
- Conditional branching (true/false path)
- Notification events (start, complete, fail, step started)
- Empty plan, not found, validation failure, max depth
