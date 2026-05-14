# Section 05 Code Review Interview

## Auto-Fixed (no user input needed)
1. **Duplicate decisions** — Added `DeduplicateByApprover()` to all 3 strategies. Groups by approver name (case-insensitive), keeps earliest `RespondedAt`.
2. **AnyOf temporal ordering** — Changed `decisions[0]` to `deduplicated.MinBy(d => d.RespondedAt)!`.
3. **AllOf membership check** — Changed `decisions.Count >= request.Approvers.Count` to `pending.Length == 0`.
4. **Immutable arrays** — Changed `.ToList()` to `.ToArray()` for `PendingApprovers` backing.

## User Decisions
5. **QuorumThreshold=0 guard clause** — User chose: guard clause. Added early return resolving as approved when `threshold <= 0`.
6. **Test helper duplication** — User chose: keep duplicated. YAGNI — each test file is self-contained.

## Let Go
- Missing test cases for edge scenarios (empty approvers, case-insensitive matching, out-of-list approvers) — deferred to section-21 comprehensive tests.
- Interface XML doc precondition contracts — the doc is already thorough for a template.
