# Section 05: Approval Strategies

## Overview

This section implements the strategy pattern for evaluating multi-approver decisions on escalation requests. Three concrete strategies cover the common enterprise approval topologies: any-one-approves (AnyOf), unanimous consent (AllOf), and majority quorum (Quorum). Each strategy evaluates a list of `ApproverDecision` records against the `EscalationRequest` parameters and returns an `ApprovalEvaluation` indicating whether the escalation is resolved.

**Layer placement:**
- `IApprovalStrategy` interface in `Application.AI.Common/Interfaces/Escalation/`
- Three implementations in `Application.Core/Escalation/Strategies/`
- Keyed DI registration by `ApprovalStrategyType` in `Application.Core/DependencyInjection.cs` (deferred to section-19)

**Dependencies:**
- **section-01-domain-escalation** must be implemented first. This section consumes these domain types:
  - `EscalationRequest` record (provides `Approvers`, `QuorumThreshold`, `ApprovalStrategy`)
  - `ApproverDecision` record (provides `ApproverName`, `Approved`, `Reason`, `RespondedAt`)
  - `ApprovalEvaluation` record (provides `IsResolved`, `IsApproved`, `PendingApprovers`)
  - `ApprovalStrategyType` enum (`AnyOf`, `AllOf`, `Quorum`)

**Blocks:** section-08-escalation-service (the `DefaultEscalationService` resolves strategies via keyed DI to evaluate incoming decisions)

---

## Tests First

All tests go in `src/Content/Tests/Application.Core.Tests/Escalation/Strategies/`. The test project already references both `Application.Core` and `Application.AI.Common` projects.

Testing framework: xUnit + FluentAssertions. Naming: `MethodName_Scenario_ExpectedResult`. Arrange-Act-Assert.

### AnyOfApprovalStrategyTests

**File:** `src/Content/Tests/Application.Core.Tests/Escalation/Strategies/AnyOfApprovalStrategyTests.cs`

```csharp
namespace Application.Core.Tests.Escalation.Strategies;

/// <summary>
/// Tests for <see cref="AnyOfApprovalStrategy"/>.
/// AnyOf resolves on the first response -- approval or denial.
/// </summary>
public class AnyOfApprovalStrategyTests
{
    // Test: EvaluateDecision_SingleApproval_ResolvesApproved
    //   Arrange: request with 3 approvers, decisions list with 1 approval
    //   Act: EvaluateDecision(request, decisions)
    //   Assert: IsResolved == true, IsApproved == true, PendingApprovers == remaining 2

    // Test: EvaluateDecision_SingleDenial_ResolvesDenied
    //   Arrange: request with 3 approvers, decisions list with 1 denial
    //   Act: EvaluateDecision(request, decisions)
    //   Assert: IsResolved == true, IsApproved == false

    // Test: EvaluateDecision_NoDecisions_NotResolved
    //   Arrange: request with 3 approvers, empty decisions list
    //   Act: EvaluateDecision(request, decisions)
    //   Assert: IsResolved == false, IsApproved == false, PendingApprovers == all 3

    // Test: EvaluateDecision_MultipleApprovers_FirstResponseWins
    //   Arrange: request with 3 approvers, decisions with 1 approval then 1 denial
    //   Act: EvaluateDecision(request, decisions)
    //   Assert: IsResolved == true, IsApproved == true (first response was approval)

    // Test: StrategyType_ReturnsAnyOf
    //   Assert: StrategyType == ApprovalStrategyType.AnyOf
}
```

### AllOfApprovalStrategyTests

**File:** `src/Content/Tests/Application.Core.Tests/Escalation/Strategies/AllOfApprovalStrategyTests.cs`

```csharp
namespace Application.Core.Tests.Escalation.Strategies;

/// <summary>
/// Tests for <see cref="AllOfApprovalStrategy"/>.
/// AllOf requires unanimous approval; a single denial resolves immediately as denied.
/// </summary>
public class AllOfApprovalStrategyTests
{
    // Test: EvaluateDecision_AllApproved_ResolvesApproved
    //   Arrange: request with 3 approvers, decisions with all 3 approved
    //   Act: EvaluateDecision(request, decisions)
    //   Assert: IsResolved == true, IsApproved == true, PendingApprovers empty

    // Test: EvaluateDecision_SingleDenialAmongMultiple_ResolvesDeniedImmediately
    //   Arrange: request with 3 approvers, decisions with 1 approval + 1 denial
    //   Act: EvaluateDecision(request, decisions)
    //   Assert: IsResolved == true, IsApproved == false (single denial is enough)

    // Test: EvaluateDecision_PartialApprovals_NotResolved
    //   Arrange: request with 3 approvers, decisions with 2 approvals
    //   Act: EvaluateDecision(request, decisions)
    //   Assert: IsResolved == false, PendingApprovers contains the 3rd approver

    // Test: EvaluateDecision_SingleApprover_ApprovesImmediately
    //   Arrange: request with 1 approver, decisions with 1 approval
    //   Act: EvaluateDecision(request, decisions)
    //   Assert: IsResolved == true, IsApproved == true

    // Test: StrategyType_ReturnsAllOf
    //   Assert: StrategyType == ApprovalStrategyType.AllOf
}
```

### QuorumApprovalStrategyTests

**File:** `src/Content/Tests/Application.Core.Tests/Escalation/Strategies/QuorumApprovalStrategyTests.cs`

```csharp
namespace Application.Core.Tests.Escalation.Strategies;

/// <summary>
/// Tests for <see cref="QuorumApprovalStrategy"/>.
/// Quorum resolves when enough votes exist to mathematically determine outcome.
/// </summary>
public class QuorumApprovalStrategyTests
{
    // Test: EvaluateDecision_QuorumMet_ResolvesApproved
    //   Arrange: request with 3 approvers, QuorumThreshold=2, decisions with 2 approvals
    //   Act: EvaluateDecision(request, decisions)
    //   Assert: IsResolved == true, IsApproved == true

    // Test: EvaluateDecision_QuorumImpossible_ResolvesDenied
    //   Arrange: request with 3 approvers, QuorumThreshold=2, decisions with 2 denials
    //   Act: EvaluateDecision(request, decisions)
    //   Assert: IsResolved == true, IsApproved == false (impossible to reach 2 approvals)

    // Test: EvaluateDecision_InsufficientVotes_NotResolved
    //   Arrange: request with 3 approvers, QuorumThreshold=2, decisions with 1 approval
    //   Act: EvaluateDecision(request, decisions)
    //   Assert: IsResolved == false

    // Test: EvaluateDecision_EdgeCase_OneOfOne_ResolvesOnFirst
    //   Arrange: request with 1 approver, QuorumThreshold=1, decisions with 1 approval
    //   Assert: IsResolved == true, IsApproved == true

    // Test: EvaluateDecision_EdgeCase_TwoOfThree_NeedsExactQuorum
    //   Arrange: request with 3 approvers, QuorumThreshold=2
    //   Subcase A: 1 approval, 1 denial -> not resolved (1 approval < 2, 1 denial < 2)
    //   Subcase B: 2 approvals -> resolved approved
    //   Subcase C: 2 denials -> resolved denied

    // Test: EvaluateDecision_ThresholdEqualsTotal_BehavesLikeAllOf
    //   Arrange: request with 3 approvers, QuorumThreshold=3
    //   Assert: behaves identically to AllOf (all must approve, single denial resolves)

    // Test: StrategyType_ReturnsQuorum
    //   Assert: StrategyType == ApprovalStrategyType.Quorum
}
```

---

## Implementation

### Interface: IApprovalStrategy

**File:** `src/Content/Application/Application.AI.Common/Interfaces/Escalation/IApprovalStrategy.cs`

This interface defines the contract for evaluating a set of approver decisions against an escalation request. Registered via keyed DI by `ApprovalStrategyType` so the `DefaultEscalationService` can resolve the correct strategy from the request's `ApprovalStrategy` field.

```csharp
using Domain.AI.Escalation;

namespace Application.AI.Common.Interfaces.Escalation;

/// <summary>
/// Evaluates approver decisions against an escalation request to determine resolution.
/// Registered via keyed DI -- resolved by <see cref="ApprovalStrategyType"/>.
/// </summary>
/// <remarks>
/// <para>Three built-in strategies:</para>
/// <list type="bullet">
///   <item><c>AnyOf</c> -- first response wins (approve or deny)</item>
///   <item><c>AllOf</c> -- unanimous approval required, single denial resolves immediately</item>
///   <item><c>Quorum</c> -- N-of-M threshold, resolved when outcome is mathematically determined</item>
/// </list>
/// </remarks>
public interface IApprovalStrategy
{
    /// <summary>
    /// Evaluates collected decisions against the request's approval requirements.
    /// </summary>
    /// <param name="request">The escalation request containing approver list and threshold config.</param>
    /// <param name="decisions">All decisions collected so far.</param>
    /// <returns>Evaluation result indicating whether the escalation is resolved and the verdict.</returns>
    ApprovalEvaluation EvaluateDecision(
        EscalationRequest request,
        IReadOnlyList<ApproverDecision> decisions);

    /// <summary>
    /// The strategy type this implementation handles. Used as the keyed DI key.
    /// </summary>
    ApprovalStrategyType StrategyType { get; }
}
```

### AnyOfApprovalStrategy

**File:** `src/Content/Application/Application.Core/Escalation/Strategies/AnyOfApprovalStrategy.cs`

Logic:
- If `decisions` is empty, return not resolved with all approvers pending.
- If any decision exists (approved or denied), the escalation is resolved. The first response determines the outcome.
- `PendingApprovers` is the set difference of `request.Approvers` minus approvers who have responded.

`StrategyType` returns `ApprovalStrategyType.AnyOf`.

The implementation is a single method body with no external dependencies -- pure function over the input records.

### AllOfApprovalStrategy

**File:** `src/Content/Application/Application.Core/Escalation/Strategies/AllOfApprovalStrategy.cs`

Logic:
- If any decision has `Approved == false`, immediately resolve as denied (`IsResolved = true`, `IsApproved = false`). One denial is enough.
- If the count of approved decisions equals the count of `request.Approvers`, resolve as approved.
- Otherwise, not resolved. `PendingApprovers` is the approvers who haven't responded yet.

`StrategyType` returns `ApprovalStrategyType.AllOf`.

### QuorumApprovalStrategy

**File:** `src/Content/Application/Application.Core/Escalation/Strategies/QuorumApprovalStrategy.cs`

Logic:
- Read `request.QuorumThreshold` for the required N in N-of-M.
- Count `approvedCount` = decisions where `Approved == true`.
- Count `deniedCount` = decisions where `Approved == false`.
- `totalApprovers` = `request.Approvers.Count`.
- If `approvedCount >= quorumThreshold`, resolve as approved.
- If `deniedCount > (totalApprovers - quorumThreshold)`, resolve as denied -- it's mathematically impossible to reach quorum. The check is: remaining possible approvals = `totalApprovers - approvedCount - deniedCount`. If `approvedCount + remaining < quorumThreshold`, deny.
- Otherwise, not resolved.

`StrategyType` returns `ApprovalStrategyType.Quorum`.

---

## Domain Types Required (from section-01)

These types must exist before implementing this section. They are defined in section-01-domain-escalation but listed here for quick reference:

**`ApprovalStrategyType`** enum in `Domain.AI/Escalation/ApprovalStrategyType.cs`:
- `AnyOf = 0`
- `AllOf = 1`
- `Quorum = 2`

**`ApprovalEvaluation`** record in `Domain.AI/Escalation/ApprovalEvaluation.cs`:
- `IsResolved` (bool) -- whether the escalation has reached a terminal state
- `IsApproved` (bool) -- the verdict (only meaningful when `IsResolved == true`)
- `PendingApprovers` (IReadOnlyList<string>) -- approvers who haven't responded yet

**`EscalationRequest`** record -- the `Approvers` (IReadOnlyList<string>), `QuorumThreshold` (int), and `ApprovalStrategy` (ApprovalStrategyType) fields are used by strategies.

**`ApproverDecision`** record -- `ApproverName` (string), `Approved` (bool) are the fields read during evaluation.

---

## DI Registration (deferred to section-19)

The three strategies are registered as keyed singletons in `Application.Core/DependencyInjection.cs`. The keyed DI pattern follows the existing convention used for `ISupervisorStrategy` and `ITool` registrations:

```csharp
// In Application.Core DependencyInjection.AddApplicationCoreDependencies():
services.AddKeyedSingleton<IApprovalStrategy>(
    ApprovalStrategyType.AnyOf,
    (_, _) => new AnyOfApprovalStrategy());

services.AddKeyedSingleton<IApprovalStrategy>(
    ApprovalStrategyType.AllOf,
    (_, _) => new AllOfApprovalStrategy());

services.AddKeyedSingleton<IApprovalStrategy>(
    ApprovalStrategyType.Quorum,
    (_, _) => new QuorumApprovalStrategy());
```

The `DefaultEscalationService` (section-08) resolves the correct strategy at runtime:
```csharp
var strategy = serviceProvider.GetRequiredKeyedService<IApprovalStrategy>(request.ApprovalStrategy);
```

---

## File Inventory

| File | Action | Layer |
|------|--------|-------|
| `src/Content/Application/Application.AI.Common/Interfaces/Escalation/IApprovalStrategy.cs` | Create | Application |
| `src/Content/Application/Application.Core/Escalation/Strategies/AnyOfApprovalStrategy.cs` | Create | Application |
| `src/Content/Application/Application.Core/Escalation/Strategies/AllOfApprovalStrategy.cs` | Create | Application |
| `src/Content/Application/Application.Core/Escalation/Strategies/QuorumApprovalStrategy.cs` | Create | Application |
| `src/Content/Tests/Application.Core.Tests/Escalation/Strategies/AnyOfApprovalStrategyTests.cs` | Create | Tests |
| `src/Content/Tests/Application.Core.Tests/Escalation/Strategies/AllOfApprovalStrategyTests.cs` | Create | Tests |
| `src/Content/Tests/Application.Core.Tests/Escalation/Strategies/QuorumApprovalStrategyTests.cs` | Create | Tests |

---

## Implementation Notes

- All three strategy classes are stateless -- no constructor dependencies, no injected services. They are pure functions over immutable domain records. This makes them trivially testable and safe as singletons.
- The `PendingApprovers` computation is shared logic across all three strategies: `request.Approvers` minus `decisions.Select(d => d.ApproverName)`. Computed in-line in each class (three copies is acceptable per YAGNI -- shared base class is premature here).
- The quorum denial check uses the formula: `approvedCount + remainingVotes < quorumThreshold`. This means if 2-of-3 is required and 2 have denied, the remaining 1 approval cannot reach threshold -- deny immediately rather than waiting for the third vote.
- Edge case: if `QuorumThreshold` is 0, any number of responses would satisfy quorum. The `EscalationConfigValidator` (section-04) should prevent `QuorumThreshold < 1` from reaching here, but a guard clause was added for defense-in-depth (resolves as approved immediately).

## Deviations from Plan

- **Deduplication added**: Code review identified that duplicate decisions from the same approver could corrupt vote counts. All 3 strategies now include `DeduplicateByApprover()` which groups by approver name (case-insensitive) and keeps the earliest `RespondedAt`.
- **AnyOf temporal ordering**: Changed from `decisions[0]` (list order) to `MinBy(d => d.RespondedAt)` (actual first response) to match the "first response wins" specification.
- **AllOf membership check**: Changed from `decisions.Count >= request.Approvers.Count` to `pending.Length == 0` to check actual approver membership rather than raw count.
- **Immutable backing**: Changed `.ToList()` to `.ToArray()` for `PendingApprovers` to avoid exposing mutable backing collection.
- **QuorumThreshold=0 guard**: Added early-return guard clause in QuorumApprovalStrategy.
- **EscalationPriority enum**: Section plan referenced `Normal` but actual enum values are `Informational`, `Blocking`, `Critical`. Tests use `Blocking`.
- **Test helpers kept duplicated**: YAGNI decision -- each test file is self-contained.

## Test Results

19 tests, all passing. 5 AnyOf + 5 AllOf + 9 Quorum (includes Theory with 2 InlineData cases).
