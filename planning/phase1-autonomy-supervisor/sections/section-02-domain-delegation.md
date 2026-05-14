# Section 2: Domain Models -- Supervisor and Delegation

## Overview

This section creates the domain primitives for task delegation in `Domain.AI/Orchestration/`. These are pure value-object records with no behavior beyond static factory methods on `DelegationResult`. They represent the vocabulary of delegation: who delegated what to whom, the lifecycle state, scoring, and results.

This section has **no dependencies** on other sections and can be implemented in parallel with Section 01 (Domain Autonomy). However, several types reference `AutonomyLevel` (from Section 01) and `SubagentType` (already exists at `Domain.AI/Agents/SubagentType.cs`). If implementing before Section 01 is complete, stub `AutonomyLevel` as a placeholder or implement Section 01 first.

Later sections that depend on these types: Section 03 (interfaces), Section 05 (capability strategy), Section 06 (JSONL store), Section 07 (supervisor implementation).

---

## Background Context

### Existing Domain Types Referenced

These types already exist in the codebase and are used by the new delegation records:

- **`SubagentType`** (`Domain.AI/Agents/SubagentType.cs`) -- Enum with values: `Explore`, `Plan`, `Verify`, `Execute`, `General`. Used by `DelegationRecord.DelegateAgentType` and `AgentCandidate.AgentType`.

- **`PermissionBehaviorType`** (`Domain.AI/Permissions/PermissionBehaviorType.cs`) -- Enum with `Allow`, `Deny`, `Ask`. Not directly used by delegation types but referenced by the `AutonomyTierPolicy` (Section 01) that `AutonomyExceededResult` relates to.

### Dependency from Section 01

- **`AutonomyLevel`** (to be created at `Domain.AI/Governance/AutonomyLevel.cs` by Section 01) -- Enum with `Restricted = 0`, `Supervised = 1`, `Autonomous = 2`. Referenced by `DelegationRecord.AutonomyLevel`, `SupervisorDecisionContext.MinimumAutonomyLevel`, `AgentCandidate.AutonomyLevel`.

- **`AutonomyExceededResult`** (to be created at `Domain.AI/Governance/AutonomyExceededResult.cs` by Section 01) -- Record with `AttemptedAction`, `CurrentLevel`, `RequiredLevel`, `Reason`. Referenced by `DelegationRecord.AutonomyExceeded` and `DelegationResult.AutonomyExceeded`.

### Design Principles

All records follow the codebase immutability conventions:
- Init-only properties (no setters)
- `IReadOnlyList<T>` for collection surfaces
- Records for value semantics
- State transitions create new records (append-only pattern for JSONL persistence)
- Static factory methods for constrained construction where behavior exists (`DelegationResult`)

---

## Tests (Write First)

These tests validate the only behavioral logic in this section: the `DelegationResult` static factory methods. The other types are pure data records with no behavior -- compilation validates their correctness.

**File:** `src/Content/Tests/Domain.AI.Tests/Orchestration/DelegationResultTests.cs`

```csharp
namespace Domain.AI.Tests.Orchestration;

/// <summary>
/// Validates DelegationResult static factory methods produce correctly
/// populated instances with expected property values.
/// </summary>
public class DelegationResultTests
{
    // Test: DelegationResult.Success creates result with IsSuccess=true,
    //       populated Output, TokensUsed, and DurationMs
    [Fact]
    public void Success_CreatesResult_WithIsSuccessTrueAndPopulatedFields()
    {
        // Arrange & Act: call DelegationResult.Success("output text", tokens: 150, durationMs: 2500)
        // Assert: IsSuccess == true, Output == "output text", TokensUsed == 150,
        //         DurationMs == 2500, FailureReason == null, AutonomyExceeded == null
    }

    // Test: DelegationResult.Fail creates result with IsSuccess=false and populated FailureReason
    [Fact]
    public void Fail_CreatesResult_WithIsSuccessFalseAndPopulatedFailureReason()
    {
        // Arrange & Act: call DelegationResult.Fail("agent timed out")
        // Assert: IsSuccess == false, FailureReason == "agent timed out",
        //         Output == null, AutonomyExceeded == null
    }

    // Test: DelegationResult.FailAutonomyExceeded creates result with populated AutonomyExceeded
    [Fact]
    public void FailAutonomyExceeded_CreatesResult_WithPopulatedAutonomyExceeded()
    {
        // Arrange: create an AutonomyExceededResult instance
        // Act: call DelegationResult.FailAutonomyExceeded(exceededResult)
        // Assert: IsSuccess == false, AutonomyExceeded == exceededResult,
        //         FailureReason contains "autonomy" (or similar indicator)
    }
}
```

No other tests are needed for this section. The remaining types (`DelegationState`, `DelegationRecord`, `SupervisorDecisionContext`, `AgentCandidate`, `AgentSelection`, `CapabilityScore`) are pure data records validated by compilation.

---

## Implementation Details

All files go in the `src/Content/Domain/Domain.AI/Orchestration/` directory. This directory does not exist yet and must be created.

### File 1: `DelegationState.cs`

**Path:** `src/Content/Domain/Domain.AI/Orchestration/DelegationState.cs`

Enum representing the lifecycle of a delegation. Values:

- `Pending` -- Delegation created, not yet started
- `InProgress` -- Agent is executing
- `Completed` -- Successfully finished
- `Failed` -- Agent failed (includes autonomy exceeded, timeout, exception)
- `Cancelled` -- Explicitly cancelled by supervisor or caller

Namespace: `Domain.AI.Orchestration`

### File 2: `DelegationRecord.cs`

**Path:** `src/Content/Domain/Domain.AI/Orchestration/DelegationRecord.cs`

Sealed record tracking a single delegation instance. All properties are init-only. State transitions produce new records (the JSONL store in Section 06 appends new lines rather than mutating).

Properties:
- `DelegationId` -- `Guid` (unique identifier for this delegation)
- `ParentDelegationId` -- `Guid?` (null for top-level, set for nested delegations)
- `SupervisorId` -- `string` (identifies the supervising agent)
- `DelegateAgentId` -- `string` (identifies the agent receiving the delegation)
- `DelegateAgentType` -- `SubagentType` (from `Domain.AI.Agents`)
- `TaskDescription` -- `string` (human-readable description of the delegated task)
- `RequiredCapabilities` -- `IReadOnlyList<string>` (tool names needed for the task)
- `ToolOverrides` -- `IReadOnlyList<string>?` (extra tools granted for this delegation)
- `AutonomyLevel` -- `AutonomyLevel` (from `Domain.AI.Governance`, created by Section 01)
- `State` -- `DelegationState`
- `DelegationDepth` -- `int` (0 for top-level, increments with nesting)
- `StartedAt` -- `DateTimeOffset`
- `CompletedAt` -- `DateTimeOffset?`
- `FailureReason` -- `string?`
- `AutonomyExceeded` -- `AutonomyExceededResult?` (populated when failure is tier-related; type from Section 01)

Uses `required` keyword for mandatory properties (`DelegationId`, `SupervisorId`, `DelegateAgentId`, `DelegateAgentType`, `TaskDescription`, `RequiredCapabilities`, `AutonomyLevel`, `State`, `DelegationDepth`, `StartedAt`).

### File 3: `DelegationResult.cs`

**Path:** `src/Content/Domain/Domain.AI/Orchestration/DelegationResult.cs`

Sealed record returned by `ISupervisor.DelegateAsync` (Section 03). Contains the outcome of a delegation plus telemetry data. This is the only type in this section with behavioral logic (static factories).

Properties:
- `IsSuccess` -- `bool`
- `Output` -- `string?`
- `FailureReason` -- `string?`
- `AutonomyExceeded` -- `AutonomyExceededResult?`
- `TokensUsed` -- `int`
- `DurationMs` -- `long`

Static factory methods:
- `Success(string output, int tokensUsed, long durationMs)` -- Creates a successful result with `IsSuccess = true`
- `Fail(string reason)` -- Creates a failed result with `IsSuccess = false`, `TokensUsed = 0`, `DurationMs = 0`
- `FailAutonomyExceeded(AutonomyExceededResult exceeded)` -- Creates a failed result with `IsSuccess = false`, the `AutonomyExceeded` property populated, and a `FailureReason` indicating the tier violation

### File 4: `SupervisorDecisionContext.cs`

**Path:** `src/Content/Domain/Domain.AI/Orchestration/SupervisorDecisionContext.cs`

Sealed record providing all inputs the supervisor strategy needs to make a selection decision. Built by the supervisor implementation (Section 07) before calling `ISupervisorStrategy.SelectAgent`.

Properties:
- `TaskDescription` -- `string`
- `RequiredCapabilities` -- `IReadOnlyList<string>`
- `MinimumAutonomyLevel` -- `AutonomyLevel`
- `AvailableAgents` -- `IReadOnlyList<AgentCandidate>`
- `CurrentDelegationDepth` -- `int`
- `MaxDelegationDepth` -- `int`

### File 5: `AgentCandidate.cs`

**Path:** `src/Content/Domain/Domain.AI/Orchestration/AgentCandidate.cs`

Sealed record describing one candidate agent for delegation selection.

Properties:
- `AgentId` -- `string`
- `AgentType` -- `SubagentType`
- `AutonomyLevel` -- `AutonomyLevel`
- `AvailableTools` -- `IReadOnlyList<string>`

### File 6: `AgentSelection.cs`

**Path:** `src/Content/Domain/Domain.AI/Orchestration/AgentSelection.cs`

Sealed record representing the strategy's chosen agent, with audit-friendly metadata.

Properties:
- `SelectedAgent` -- `AgentCandidate`
- `ConfidenceScore` -- `double` (0.0 to 1.0)
- `Reasoning` -- `string` (human-readable explanation for audit trail)

### File 7: `CapabilityScore.cs`

**Path:** `src/Content/Domain/Domain.AI/Orchestration/CapabilityScore.cs`

Sealed record holding the breakdown of a candidate's scoring during capability matching (Section 05). Used for observability and debugging.

Properties:
- `AgentId` -- `string`
- `ToolCoverage` -- `double` (0.0 to 1.0, ratio of required tools the agent has)
- `TypeAlignment` -- `double` (0.0 to 1.0, how well agent type matches task category)
- `TierHeadroom` -- `double` (0.0 to 1.0, how much tier headroom above the minimum)
- `TotalScore` -- `double` (weighted composite of the three factors)

---

## File Checklist

| # | File Path | Type | Action |
|---|-----------|------|--------|
| 1 | `src/Content/Domain/Domain.AI/Orchestration/DelegationState.cs` | Enum | Create |
| 2 | `src/Content/Domain/Domain.AI/Orchestration/DelegationRecord.cs` | Record | Create |
| 3 | `src/Content/Domain/Domain.AI/Orchestration/DelegationResult.cs` | Record | Create |
| 4 | `src/Content/Domain/Domain.AI/Orchestration/SupervisorDecisionContext.cs` | Record | Create |
| 5 | `src/Content/Domain/Domain.AI/Orchestration/AgentCandidate.cs` | Record | Create |
| 6 | `src/Content/Domain/Domain.AI/Orchestration/AgentSelection.cs` | Record | Create |
| 7 | `src/Content/Domain/Domain.AI/Orchestration/CapabilityScore.cs` | Record | Create |
| 8 | `src/Content/Tests/Domain.AI.Tests/Orchestration/DelegationResultTests.cs` | Test | Create |

---

## Namespace and Using Conventions

All types use namespace `Domain.AI.Orchestration`. The following usings are needed across the files:

- `DelegationRecord` needs: `Domain.AI.Agents` (for `SubagentType`), `Domain.AI.Governance` (for `AutonomyLevel`, `AutonomyExceededResult`)
- `DelegationResult` needs: `Domain.AI.Governance` (for `AutonomyExceededResult`)
- `SupervisorDecisionContext` needs: `Domain.AI.Governance` (for `AutonomyLevel`)
- `AgentCandidate` needs: `Domain.AI.Agents` (for `SubagentType`), `Domain.AI.Governance` (for `AutonomyLevel`)
- `DelegationState`, `AgentSelection`, `CapabilityScore` have no external domain dependencies

The test file needs: `Domain.AI.Orchestration`, `Domain.AI.Governance`, and the xUnit/FluentAssertions namespaces.

---

## Verification

After implementation, run:

```
dotnet build src/Content/Domain/Domain.AI/Domain.AI.csproj
dotnet test src/Content/Tests/Domain.AI.Tests/Domain.AI.Tests.csproj --filter "FullyQualifiedName~DelegationResultTests"
```

All three `DelegationResult` factory tests should pass. All seven new files should compile without error (assuming Section 01's `AutonomyLevel` and `AutonomyExceededResult` are available).
