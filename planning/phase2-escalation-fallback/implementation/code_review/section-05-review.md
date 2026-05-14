# Code Review: Section 05 -- Approval Strategies

**Reviewer:** claude-code-reviewer  
**Date:** 2026-05-09  
**Scope:** 7 new files (1 interface, 3 strategy implementations, 3 test classes, 19 tests)  
**Verdict:** **Block** -- 2 HIGH issues (correctness bugs in vote counting and decision ordering).

---

## HIGH -- Duplicate decisions from the same approver corrupt vote counts

**Files:** `AllOfApprovalStrategy.cs`, `QuorumApprovalStrategy.cs`

All three strategies count votes from the raw `decisions` list without deduplicating by approver name. If two decisions arrive for the same approver (e.g., a retry, a UI double-click, or a race condition), the counts are wrong.

**AllOf concrete example:**
```
Approvers: ["alice", "bob", "carol"] (3 required)
Decisions: [Approve("alice"), Approve("alice"), Approve("bob")]
decisions.Count (3) >= request.Approvers.Count (3) -> IsResolved=true, IsApproved=true
```
Only 2 of 3 approvers actually responded, but AllOf reports unanimous approval.

**Quorum concrete example:**
```
Approvers: ["alice", "bob", "carol"], QuorumThreshold: 2
Decisions: [Approve("alice"), Approve("alice")]
approvedCount (2) >= quorumThreshold (2) -> IsResolved=true, IsApproved=true
```
Only 1 approver voted, but Quorum reports quorum met.

**Fix:** Deduplicate decisions by approver name (last-wins or first-wins) at the top of each strategy, or better, in a shared helper. Example:

```csharp
// In each EvaluateDecision method, replace raw decisions with deduplicated set:
var uniqueDecisions = decisions
    .GroupBy(d => d.ApproverName, StringComparer.OrdinalIgnoreCase)
    .Select(g => g.Last()) // last-wins semantic
    .ToList();
```

Then use `uniqueDecisions` for all counting. The `respondedNames` HashSet already deduplicates for the pending calculation -- the vote counting must match.

---

## HIGH -- AnyOf uses list position, not temporal ordering

**File:** `AnyOfApprovalStrategy.cs:23`

```csharp
var firstDecision = decisions[0];
```

The XML doc says "first response wins" but the strategy returns `decisions[0]` -- the first element in the list, not the earliest `RespondedAt`. If the caller passes decisions in insertion order rather than chronological order, the wrong decision wins.

**Fix -- option A (recommended):** Sort by `RespondedAt` before picking:

```csharp
var firstDecision = decisions.MinBy(d => d.RespondedAt)
    ?? throw new InvalidOperationException("Decisions list is empty after non-empty check.");
```

**Fix -- option B:** Document that the caller must pass decisions sorted by `RespondedAt`. Add this precondition to the `IApprovalStrategy.EvaluateDecision` XML doc.

Option A is safer for a template project -- it makes the strategy self-contained rather than relying on caller discipline.

---

## MEDIUM -- AllOf "all responded" check uses count instead of set membership

**File:** `AllOfApprovalStrategy.cs:32`

```csharp
var allResponded = decisions.Count >= request.Approvers.Count;
```

This checks if the number of decisions equals or exceeds the number of approvers, but does not verify the decisions come from the actual approvers in the list. A decision from "dave" (not in Approvers) would inflate the count.

After the deduplication fix (HIGH #1), this should use the `respondedNames` set instead:

```csharp
var allResponded = request.Approvers.All(a => respondedNames.Contains(a));
```

This is both correct and self-documenting.

---

## MEDIUM -- `PendingApprovers` backed by mutable `List<string>`

**Files:** All three strategy implementations

```csharp
var pending = request.Approvers.Where(a => !respondedNames.Contains(a)).ToList();
```

`.ToList()` returns a `List<string>` assigned to `IReadOnlyList<string>`. A caller can cast to `List<string>` and mutate the backing collection. This violates the project immutability-first principle.

**Fix:** Use `.ToArray()` (cheaper allocation, truly immutable backing):

```csharp
var pending = request.Approvers.Where(a => !respondedNames.Contains(a)).ToArray();
```

---

## MEDIUM -- No validation or documentation for QuorumThreshold edge values

**File:** `QuorumApprovalStrategy.cs`

If `QuorumThreshold` is 0, the strategy immediately returns `IsResolved=true, IsApproved=true` with zero decisions (`0 >= 0`). If `QuorumThreshold` exceeds `Approvers.Count`, quorum is impossible from the start.

Neither case is guarded here or validated upstream (the `EscalationConfigValidator` from Section 04 validates config defaults but not per-request values).

**Options:**
1. **Recommended:** Add a guard clause at the top of `EvaluateDecision`:
   ```csharp
   if (request.QuorumThreshold <= 0 || request.QuorumThreshold > request.Approvers.Count)
       throw new ArgumentOutOfRangeException(nameof(request),
           $"QuorumThreshold must be between 1 and {request.Approvers.Count}.");
   ```
2. **Alternative:** Add a `<remarks>` block documenting that `QuorumThreshold` must be validated before reaching the strategy, and ensure FluentValidation catches it on the `EscalationRequest` creation path.

---

## MEDIUM -- Test helpers duplicated across 3 test files

**Files:** `AllOfApprovalStrategyTests.cs`, `AnyOfApprovalStrategyTests.cs`, `QuorumApprovalStrategyTests.cs`

`CreateRequest(...)`, `Approve(name)`, and `Deny(name)` are copy-pasted across all three files. This is a maintenance risk -- if `EscalationRequest` adds a new required property, all three must be updated.

**Fix:** Extract to a shared `ApprovalStrategyTestHelpers` class in the test project:

```csharp
internal static class ApprovalStrategyTestHelpers
{
    public static EscalationRequest CreateRequest(
        string[] approvers,
        ApprovalStrategyType strategy = ApprovalStrategyType.AnyOf,
        int quorumThreshold = 0) => new() { ... };

    public static ApproverDecision Approve(string name) => new() { ... };
    public static ApproverDecision Deny(string name) => new() { ... };
}
```

---

## LOW -- Missing test cases

**Files:** All three test classes

| Missing test | Strategy | Why it matters |
|---|---|---|
| Empty decisions list | AllOf, Quorum | AllOf returns `IsResolved=false` (correct), but untested. Quorum with threshold=0 silently approves. |
| Duplicate decisions from same approver | All | Currently a correctness bug (HIGH #1). After fix, test the dedup behavior. |
| Case-insensitive approver matching | All | The `respondedNames` HashSet uses `OrdinalIgnoreCase`, but no test verifies "Alice" matches "alice". |
| Decision from approver not in Approvers list | All | Currently inflates counts (MEDIUM #3). After fix, should be ignored or tested. |
| `QuorumThreshold = 0` | Quorum | Vacuous approval with zero votes (MEDIUM #4). |
| `QuorumThreshold > Approvers.Count` | Quorum | Impossible quorum -- always denied. |

---

## LOW -- `IApprovalStrategy.EvaluateDecision` XML doc lacks precondition contract

**File:** `IApprovalStrategy.cs:28`

The `decisions` parameter doc says "All decisions collected so far" but does not specify:
- Whether decisions must be deduplicated (they should be, per HIGH #1)
- Whether decisions must be sorted chronologically (relevant for AnyOf, per HIGH #2)
- Whether decisions from approvers not in the request `Approvers` list are valid

For a template project, these contracts are teaching material. Add a `<remarks>` block:

```xml
/// <remarks>
/// Implementations should handle duplicate decisions (same approver responding multiple times)
/// and decisions from unknown approvers gracefully. The caller is not required to pre-filter
/// or sort the decisions list.
/// </remarks>
```

---

## INFO -- Logic correctness verified

- **Quorum denial math** is correct: `approvedCount + remainingVotes < quorumThreshold` correctly identifies when quorum is mathematically impossible even if all remaining voters approve.
- **AllOf early termination** on first denial is correct.
- **Case-insensitive pending calculation** via `StringComparer.OrdinalIgnoreCase` is consistently applied across all three strategies.
- **Clean Architecture compliance:** Interface in Application.AI.Common, implementations in Application.Core. No Infrastructure or Domain leakage.
- **File sizes:** All files well under 150 lines. Strategies are 40-56 lines each.
- **No secrets, no injection risks, no I/O:** Pure computation over domain types.

---

## Summary

| Severity | Count | Action |
|----------|-------|--------|
| CRITICAL | 0 | -- |
| HIGH | 2 | Must fix before merge |
| MEDIUM | 4 | Should fix |
| LOW | 2 | Consider improving |
| INFO | 1 | Awareness only |

**Verdict: Block.** The two HIGH issues are correctness bugs -- duplicate decisions can produce wrong verdicts, and AnyOf "first wins" semantic depends on list ordering rather than temporal ordering. Both are fixable with small, localized changes. The MEDIUM items (count vs. set membership, mutable list backing, quorum edge cases, test duplication) should be addressed in the same pass.

**Recommended fix order:**
1. Deduplicate decisions by approver name in all strategies (or extract to shared helper)
2. AnyOf: use `MinBy(d => d.RespondedAt)` instead of `decisions[0]`
3. AllOf: use `respondedNames` set for "all responded" check instead of count comparison
4. Replace `.ToList()` with `.ToArray()` for PendingApprovers
5. Extract test helpers to shared class
6. Add missing test cases (especially for the bugs being fixed)
7. Add precondition remarks to `IApprovalStrategy` interface
