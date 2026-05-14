# Section 06 — Code Review Interview Transcript

**Date:** 2026-05-11
**Section:** section-06-learnings-interfaces
**Review file:** section-06-review.md

---

## Triage Summary

| ID | Severity | Finding | Disposition |
|----|----------|---------|-------------|
| CRIT-01 | Critical | Missing Provenance.Confidence [0,1] validation in RememberCommandValidator | Auto-fix |
| HIGH-01 | High | Missing Source/Provenance NotNull validation in RememberCommandValidator | Auto-fix |
| WARN-01 | Warning | Missing Scope validation in RecallQueryValidator | Auto-fix |

---

## Auto-Fixes Applied

### CRIT-01: Missing Provenance.Confidence validation
- **File:** `RememberCommandValidator.cs`
- **Fix:** Added `InclusiveBetween(0.0, 1.0)` rule for `Provenance.Confidence` with `.When(x => x.Provenance is not null)` guard
- **Test added:** `Validate_RememberCommand_ProvenanceConfidenceOutOfRange_HasError` (Theory with -0.1, 1.1, 2.0)

### HIGH-01: Missing Source/Provenance NotNull validation
- **File:** `RememberCommandValidator.cs`
- **Fix:** Added `NotNull()` rules for both `Source` and `Provenance`
- **Tests added:** `Validate_RememberCommand_NullSource_HasError`, `Validate_RememberCommand_NullProvenance_HasError`

### WARN-01: Missing Scope validation on RecallQuery
- **File:** `RecallQueryValidator.cs`
- **Fix:** Added `.Must()` rule requiring AgentId, TeamId, or IsGlobal — matches RememberCommandValidator pattern
- **Test added:** `Validate_RecallQuery_EmptyScope_HasError`

---

## Items Let Go

No nitpick or low-severity items were flagged.

---

## Verification

- Build: 0 errors, 142 warnings (pre-existing)
- Tests: 50 passed (Learnings suite), 0 failed
- New tests: 6 added (3 RememberCommand + 1 Theory×3 + 1 RecallQuery scope)
