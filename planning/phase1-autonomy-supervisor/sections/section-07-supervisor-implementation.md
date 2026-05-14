# Section 7: Supervisor Implementation -- `CapabilityMatchSupervisor`

## Overview

This section implements `CapabilityMatchSupervisor`, the core orchestrator that ties together strategy selection, delegation execution, state tracking, and depth management. It implements the `ISupervisor` interface defined in Section 3.

The supervisor coordinates multiple agents on a task by: selecting the best agent via `ISupervisorStrategy`, creating a delegation record, executing the selected agent with proper context (tools, tier, depth), persisting state transitions, and returning a structured `DelegationResult`.

## Dependencies on Other Sections

| Section | What This Section Uses |
|---------|----------------------|
| Section 1 (Domain: Autonomy) | `AutonomyLevel` enum, `AutonomyExceededResult` record |
| Section 2 (Domain: Delegation) | `DelegationState`, `DelegationRecord`, `DelegationResult`, `SupervisorDecisionContext`, `AgentCandidate`, `AgentSelection`, `CapabilityScore` |
| Section 3 (Interfaces) | `ISupervisor`, `ISupervisorStrategy`, `IDelegationStore`, `IAutonomyTierResolver`. Also the modified `AgentExecutionContext` with `DelegationDepth`, `DelegationId`, `DelegatingAgentType` properties, and the `CreateFromDelegation` factory method on `AgentExecutionContextFactory`. |
| Section 4 (Tier Rule Provider) | Not directly consumed, but its rules flow through the permission system when the delegated agent executes tools. |
| Section 5 (Capability Strategy) | `CapabilityMatchStrategy` implementing `ISupervisorStrategy`, resolved via keyed DI key `"capability-match"`. |
| Section 6 (JSONL Store) | `JsonlDelegationStore` implementing `IDelegationStore`. |

## File to Create

**`src/Content/Infrastructure/Infrastructure.AI/Agents/CapabilityMatchSupervisor.cs`**

Namespace: `Infrastructure.AI.Agents`

## Existing Files to Understand (Read-Only Context)

These files are already in the codebase and are consumed by this implementation. Do not modify them in this section:

- `src/Content/Application/Application.AI.Common/Interfaces/Agents/ISupervisor.cs` -- the interface being implemented (Section 3)
- `src/Content/Application/Application.AI.Common/Interfaces/Agents/ISupervisorStrategy.cs` -- agent selection strategy (Section 3)
- `src/Content/Application/Application.AI.Common/Interfaces/Agents/IDelegationStore.cs` -- delegation persistence (Section 3)
- `src/Content/Application/Application.AI.Common/Interfaces/Governance/IAutonomyTierResolver.cs` -- tier lookup (Section 3)
- `src/Content/Application/Application.AI.Common/Interfaces/Agents/ISubagentProfileRegistry.cs` -- enumerates available agent profiles
- `src/Content/Application/Application.AI.Common/Interfaces/Agents/ISubagentToolResolver.cs` -- resolves tool pools for subagents
- `src/Content/Application/Application.AI.Common/Interfaces/Governance/IGovernanceAuditService.cs` -- tamper-evident audit logging
- `src/Content/Application/Application.AI.Common/Factories/AgentExecutionContextFactory.cs` -- builds `AgentExecutionContext` from definitions
- `src/Content/Application/Application.AI.Common/Interfaces/IAgentFactory.cs` -- creates running `AIAgent` instances
- `src/Content/Domain/Domain.AI/Agents/AgentExecutionContext.cs` -- runtime agent config (modified in Section 3 to add delegation properties)
- `src/Content/Domain/Domain.AI/Agents/SubagentDefinition.cs` -- agent profile config (modified in Section 1 to add `AutonomyLevel`)
- `src/Content/Domain/Domain.Common/Config/AI/Orchestration/SubagentConfig.cs` -- config (modified in Section 8 to add delegation settings)

## Constructor Dependencies

The supervisor takes these injected services:

```csharp
public CapabilityMatchSupervisor(
    [FromKeyedServices("capability-match")] ISupervisorStrategy strategy,
    IDelegationStore delegationStore,
    ISubagentProfileRegistry profileRegistry,
    ISubagentToolResolver toolResolver,
    IAutonomyTierResolver tierResolver,
    IGovernanceAuditService auditService,
    AgentExecutionContextFactory contextFactory,
    IAgentFactory agentFactory,
    IOptionsMonitor<AppConfig> options,
    ILogger<CapabilityMatchSupervisor> logger)
```

Key notes:
- `ISupervisorStrategy` is resolved via keyed DI (`"capability-match"`). Use `[FromKeyedServices("capability-match")]` attribute on the constructor parameter.
- `AgentExecutionContextFactory` is used to build the `AgentExecutionContext` for the delegated agent via its `CreateFromDelegation` method (added in Section 3).
- `IAgentFactory` is used to create the running `AIAgent` from the context.
- Config values come from `AppConfig.AI.Orchestration.Subagent` (properties added in Section 8): `MaxDelegationDepth`, `DelegationTimeoutSeconds`, `MaxConcurrentDelegations`.

## Internal State

The supervisor maintains two pieces of mutable state:

1. **Concurrency semaphore**: `SemaphoreSlim` initialized to `MaxConcurrentDelegations` (from config, default 5). Acquired at the top of `DelegateAsync`, released in a `finally` block. Callers that exceed capacity block (with timeout) until a slot opens.

2. **Active cancellation sources**: `ConcurrentDictionary<Guid, CancellationTokenSource>` mapping delegation IDs to their `CancellationTokenSource`. Entries are added when a delegation starts executing (step 6), removed in the `finally` block when delegation completes (success, failure, or cancellation). `CancelDelegationAsync` looks up the source by ID and calls `Cancel()`.

## Execution Flow: `DelegateAsync`

This is the primary method. Step-by-step:

### Step 1: Build `SupervisorDecisionContext`

Enumerate all available agent profiles from `ISubagentProfileRegistry.GetAllProfiles()`. For each profile:
- Resolve the tool set via `ISubagentToolResolver.ResolveToolsForSubagent(definition, parentTools)`. The parent tools come from the supervisor's own context (or all registered tools if the supervisor is top-level). Extract tool names as `IReadOnlyList<string>`.
- Resolve the autonomy level via `IAutonomyTierResolver.Resolve(definition)`.
- Build an `AgentCandidate` record.

Determine `CurrentDelegationDepth`: if this supervisor is itself running within a delegation, read it from the ambient `AgentExecutionContext.DelegationDepth` (the scoped context set by `AgentContextPropagationBehavior`). If null, it is 0 (top-level).

Read `MaxDelegationDepth` from `IOptionsMonitor<AppConfig>.CurrentValue.AI.Orchestration.Subagent.MaxDelegationDepth` (default 3, added in Section 8).

Construct `SupervisorDecisionContext` with the task description, required capabilities, minimum autonomy level, list of `AgentCandidate`, current depth, and max depth.

### Step 2: Check Depth Limit

If `CurrentDelegationDepth >= MaxDelegationDepth`, return `DelegationResult.Fail($"Delegation depth limit ({MaxDelegationDepth}) exceeded.")` immediately. Do not acquire the concurrency semaphore for this check -- it is a fast-path rejection.

### Step 3: Select Agent via Strategy

Call `ISupervisorStrategy.SelectAgent(context)`. This is synchronous (pure function over in-memory data).

If the strategy returns `null` (no suitable agent), return `DelegationResult.Fail("No capable agent found for the requested task and tier requirements.")`.

### Step 4: Create Pending Delegation Record

Build a `DelegationRecord` with:
- `DelegationId` = `Guid.NewGuid()`
- `ParentDelegationId` = read from ambient `AgentExecutionContext.DelegationId` (null if top-level)
- `SupervisorId` = this supervisor's agent ID (from ambient context or config)
- `DelegateAgentId` = selected agent's `AgentId`
- `DelegateAgentType` = selected agent's `AgentType`
- `TaskDescription` = the input task description
- `RequiredCapabilities` = the input required capabilities list
- `ToolOverrides` = the input tool overrides (nullable)
- `AutonomyLevel` = selected agent's `AutonomyLevel`
- `State` = `DelegationState.Pending`
- `DelegationDepth` = `CurrentDelegationDepth + 1`
- `StartedAt` = `DateTimeOffset.UtcNow`

Append to `IDelegationStore`.

### Step 5: Audit the Decision

Call `IGovernanceAuditService.Log()` with:
- `agentId` = the supervisor's agent ID
- `action` = `$"delegate:{selectedAgent.AgentId}"`
- `decision` = `$"selected (score: {selection.ConfidenceScore:F2}, reason: {selection.Reasoning})"`

### Step 6: Execute the Delegated Agent

This is the core execution block, wrapped in the concurrency semaphore and try/catch/finally:

1. **Acquire semaphore**: `await _semaphore.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), ct)`. If the wait times out (returns false), return `DelegationResult.Fail("Delegation concurrency limit reached; timed out waiting for a slot.")`.

2. **Build agent context**: Call `AgentExecutionContextFactory.CreateFromDelegation(subagentDefinition, toolOverrides, delegationDepth, delegationId)`. This factory method (added in Section 3) creates an `AgentExecutionContext` with:
   - Tools resolved from the subagent definition plus any tool overrides
   - `DelegationDepth` = current + 1
   - `DelegationId` = the new delegation's GUID
   - `DelegatingAgentType` = the selected agent's `SubagentType`

3. **Create linked cancellation**: `CancellationTokenSource.CreateLinkedTokenSource(ct)` with a timeout via `cts.CancelAfter(TimeSpan.FromSeconds(DelegationTimeoutSeconds))`. Store in `_activeDelegations[delegationId] = cts`.

4. **Create and run agent**: Call `IAgentFactory.CreateAgentAsync(agentContext, cts.Token)` to get the `AIAgent`. Execute it against the task. The exact execution mechanism depends on the MAF agent's `InvokeAsync` or equivalent -- this section should use whatever pattern the existing codebase uses for running agents (likely through MediatR or direct MAF invocation).

5. **Capture result**: On success, extract the output text, token usage, and duration. Build `DelegationResult.Success(output, tokensUsed, durationMs)`.

### Step 7: Handle Outcomes

**On success:**
- Create a new `DelegationRecord` with `State = DelegationState.Completed`, `CompletedAt = DateTimeOffset.UtcNow`.
- Append to `IDelegationStore`.
- Audit the outcome.

**On failure (general exception):**
- Create a new `DelegationRecord` with `State = DelegationState.Failed`, `FailureReason = ex.Message`.
- Append to store. Audit.
- Return `DelegationResult.Fail(ex.Message)`.

**On autonomy exceeded:**
- If the agent execution throws or returns an autonomy-related failure, populate `AutonomyExceededResult` on the failure record.
- Return `DelegationResult.FailAutonomyExceeded(exceededResult)`.

**On cancellation (OperationCanceledException):**
- Create a new `DelegationRecord` with `State = DelegationState.Cancelled`.
- Append to store. Audit.
- Return `DelegationResult.Fail("Delegation was cancelled.")`.

**On timeout:**
- The linked `CancellationTokenSource.CancelAfter()` triggers an `OperationCanceledException`. Handle the same as cancellation, but with reason `"Delegation timed out after {DelegationTimeoutSeconds} seconds."`.

### Step 8: Cleanup (finally block)

- Remove the `CancellationTokenSource` from `_activeDelegations` and dispose it.
- Release the semaphore.

## Other `ISupervisor` Methods

### `GetDelegationStatusAsync(Guid delegationId, CancellationToken ct)`

Delegates directly to `IDelegationStore.GetByIdAsync(delegationId, ct)`.

### `GetActiveDelegationsAsync(CancellationToken ct)`

Get the supervisor's own agent ID, then call `IDelegationStore.GetBySessionAsync(supervisorId, ct)`. Filter to records where `State` is `Pending` or `InProgress`.

### `CancelDelegationAsync(Guid delegationId, CancellationToken ct)`

1. Look up `delegationId` in `_activeDelegations`.
2. If found, call `Cancel()` on the `CancellationTokenSource`. Return `true`.
3. If not found (delegation already completed or unknown ID), return `false`.

The actual state transition to `Cancelled` happens in the execution flow's catch block for `OperationCanceledException` (Step 7).

## Multi-Level Delegation

When an agent running inside a delegation itself calls `ISupervisor.DelegateAsync`:

- The `CurrentDelegationDepth` is read from `AgentExecutionContext.DelegationDepth` (typed property added in Section 3, not magic strings in `AdditionalProperties`).
- `ParentDelegationId` is set from `AgentExecutionContext.DelegationId`, linking child to parent.
- At `MaxDelegationDepth`, the depth check in Step 2 rejects further nesting with a structured error.

This creates a chain: Supervisor (depth 0) -> Agent A (depth 1) -> Agent B (depth 2). If `MaxDelegationDepth = 3`, Agent B can delegate once more but its sub-delegate cannot.

## Concurrency Design

The `SemaphoreSlim` is initialized once in the constructor from config. It limits how many delegations execute concurrently across all callers of this supervisor instance. Since `CapabilityMatchSupervisor` is registered as singleton (Section 8), this is a process-wide limit.

The semaphore is acquired **after** the depth check and strategy selection (Steps 2-3) but **before** agent execution (Step 6). This means the cheap validation and selection logic runs without holding a concurrency slot.

## Disposability

The supervisor should implement `IDisposable` to clean up:
- The `SemaphoreSlim`.
- Any remaining `CancellationTokenSource` instances in `_activeDelegations` (edge case: process shutdown during active delegations).

---

## Tests

All tests go in `src/Content/Tests/Infrastructure.AI.Tests/Agents/CapabilityMatchSupervisorTests.cs`.

Framework: xUnit + Moq + FluentAssertions. Test naming: `MethodName_Scenario_ExpectedResult`.

### Test Class Setup

The test class needs mocks for all constructor dependencies:

```csharp
/// <summary>
/// Unit tests for CapabilityMatchSupervisor.
/// Mocks all dependencies; tests the orchestration logic, not the strategy or store.
/// </summary>
public sealed class CapabilityMatchSupervisorTests : IDisposable
{
    // Mock all dependencies: ISupervisorStrategy, IDelegationStore, ISubagentProfileRegistry,
    // ISubagentToolResolver, IAutonomyTierResolver, IGovernanceAuditService,
    // AgentExecutionContextFactory, IAgentFactory, IOptionsMonitor<AppConfig>, ILogger.
    // Build a default AppConfig with SubagentConfig containing:
    //   MaxDelegationDepth = 3, DelegationTimeoutSeconds = 30,
    //   MaxConcurrentDelegations = 5.
    // Configure ISubagentProfileRegistry.GetAllProfiles() to return a default set
    // of SubagentDefinitions (at least one per SubagentType).
    // Configure IAutonomyTierResolver to return Supervised by default.
    // Configure ISubagentToolResolver to return a default tool list.
}
```

### Happy Path Tests

```csharp
// Test: DelegateAsync_SuccessfulDelegation_ReturnsDelegationResultSuccess
//   Arrange: Strategy returns an AgentSelection. AgentFactory creates an agent that
//            completes successfully with output "done".
//   Act: Call DelegateAsync with a task, capabilities, and minimum tier.
//   Assert: Result.IsSuccess == true, Result.Output == "done",
//           Result.TokensUsed > 0, Result.DurationMs > 0.

// Test: DelegateAsync_RecordsPendingThenCompletedToStore
//   Arrange: Same as happy path.
//   Assert: IDelegationStore.AppendAsync was called exactly twice.
//           First call has State == Pending, second call has State == Completed.
//           Verify via Moq Callback capturing the DelegationRecord arguments.

// Test: DelegateAsync_EmitsAuditEvents
//   Arrange: Same as happy path.
//   Assert: IGovernanceAuditService.Log called at least twice --
//           once for the delegation decision, once for the outcome.
```

### Failure Path Tests

```csharp
// Test: DelegateAsync_DepthExceeded_ReturnsFailWithDepthExceededReason
//   Arrange: Configure ambient context so DelegationDepth is already at MaxDelegationDepth (3).
//   Act: Call DelegateAsync.
//   Assert: Result.IsSuccess == false, Result.FailureReason contains "depth".
//           IDelegationStore.AppendAsync was NOT called (fast-path rejection).

// Test: DelegateAsync_NoCapableAgent_ReturnsFailWithNoAgentReason
//   Arrange: Strategy.SelectAgent returns null.
//   Act: Call DelegateAsync.
//   Assert: Result.IsSuccess == false, Result.FailureReason contains "No capable agent".

// Test: DelegateAsync_AgentFailsWithAutonomyExceeded_PropagatesResult
//   Arrange: Agent execution throws/returns with an AutonomyExceededResult.
//   Assert: Result.IsSuccess == false, Result.AutonomyExceeded is not null,
//           Result.AutonomyExceeded.CurrentLevel and RequiredLevel are populated.

// Test: DelegateAsync_AgentTimesOut_ReturnsFail
//   Arrange: Set DelegationTimeoutSeconds to a very small value (e.g., 0 or 1).
//            Agent execution delays longer than the timeout.
//   Assert: Result.IsSuccess == false, Result.FailureReason contains "timed out" or "cancel".
```

### Tool Override Tests

```csharp
// Test: DelegateAsync_WithToolOverrides_OverridesAppliedToAgentContext
//   Arrange: Pass toolOverrides = ["extra_tool_a", "extra_tool_b"].
//   Assert: AgentExecutionContextFactory.CreateFromDelegation was called with the
//           tool overrides list. Verify via Moq argument capture.
```

### Concurrency Tests

```csharp
// Test: DelegateAsync_MaxConcurrentReached_BlocksUntilSlotFree
//   Arrange: Set MaxConcurrentDelegations to 1. Start a long-running delegation that
//            completes after a signal. Start a second delegation concurrently.
//   Assert: Second delegation blocks until the first completes, then succeeds.
//           Both delegations eventually return success.
//   Note: Use TaskCompletionSource to control agent execution timing.
```

### Cancellation Tests

```csharp
// Test: CancelDelegationAsync_ActiveDelegation_PropagatesCancellation
//   Arrange: Start a delegation with a long-running agent (blocks on TaskCompletionSource).
//            Wait briefly until delegation is in progress.
//   Act: Call CancelDelegationAsync with the delegation ID.
//   Assert: The delegation returns a failure result. The DelegationRecord appended to
//           the store has State == Cancelled.

// Test: CancelDelegationAsync_UnknownDelegationId_ReturnsFalse
//   Arrange: No active delegations.
//   Act: Call CancelDelegationAsync with a random GUID.
//   Assert: Returns false.
```

### Multi-Level Delegation Tests

```csharp
// Test: DelegateAsync_SetsDepthPlusOneInChildContext
//   Arrange: Current ambient DelegationDepth = 1.
//   Act: Call DelegateAsync.
//   Assert: AgentExecutionContextFactory.CreateFromDelegation was called with
//           delegationDepth = 2. Verify via argument capture.

// Test: DelegateAsync_SetsParentDelegationIdInChildRecord
//   Arrange: Current ambient DelegationId = some known GUID.
//   Act: Call DelegateAsync.
//   Assert: The DelegationRecord appended to the store has ParentDelegationId ==
//           the parent's DelegationId.
```

## Design Notes

### Why Singleton Registration

`CapabilityMatchSupervisor` is registered as singleton (Section 8) because:
- The `SemaphoreSlim` for concurrency limiting must be shared across all callers.
- The `ConcurrentDictionary` for active cancellation sources is process-wide.
- All injected dependencies (`IDelegationStore`, `ISubagentProfileRegistry`, etc.) are also singletons.

### Why Not MediatR

The supervisor does not use MediatR for delegation dispatch. The delegation is an infrastructure-level orchestration concern, not a CQRS command. The agent execution already goes through MediatR pipeline behaviors (permission checking, audit, etc.) when the delegated agent invokes tools.

### Error Classification

The supervisor distinguishes three failure categories:
1. **Autonomy exceeded** -- agent tried something above its tier. Carries `AutonomyExceededResult` with structured information about what was attempted and what tier was needed.
2. **Operational failure** -- agent threw an exception, timed out, or returned an error. Carries `FailureReason` string.
3. **Delegation infrastructure failure** -- depth exceeded, no capable agent, concurrency timeout. These are returned before any agent executes.

### Agent Execution Mechanism

The exact mechanism for "running" the delegated agent depends on how the codebase's MAF integration works. The supervisor:
1. Calls `AgentExecutionContextFactory.CreateFromDelegation(...)` to get an `AgentExecutionContext`.
2. Calls `IAgentFactory.CreateAgentAsync(context, ct)` to get an `AIAgent` instance.
3. Invokes the agent with the task description as the initial user message.

The agent execution should respect the linked `CancellationToken` so timeouts and cancellations propagate correctly. The output is extracted from the agent's response, and token/duration metrics are captured from the execution.
