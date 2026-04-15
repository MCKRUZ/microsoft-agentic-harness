# Code Review Interview: Section-05 OTel Spans

## Review verdict: APPROVE WITH FIXES — all auto-fixed

No user input required. All findings were clear improvements with no real trade-offs.

---

## Findings Disposition

### M-1: Unset status defaults to "success" — partial/timeout/blocked unreachable on spans
**Decision: Auto-fix (add comment)**
Added inline comment to the switch expression and block explaining that span-level result
categories are intentionally narrower than ExecutionTraceRecord categories. The "partial",
"timeout", and "blocked" values are set by ToolDiagnosticsMiddleware on the record, not
through this processor. Unset/Unspecified defaults to "success" (tool completed without
explicit error signal).

### M-2: Input hash reads tool arguments that may contain PII
**Decision: Auto-fix (add comment)**
Added comment clarifying that PiiFilteringProcessor runs at pipeline position 1, before
this processor at position 5, so ToolCallArguments is already scrubbed before the hash
is computed. No code change needed — existing ordering is correct.

### M-3: Missing iteration baggage promotion tests
**Decision: Auto-fix (add 2 tests)**
Added:
- `OnEnd_WhenIterationOnContext_AddsIterationTag` — baggage "3" → tag "3"
- `OnEnd_WhenNoIteration_DoesNotAddIterationTag` — no baggage → tag absent
Total test count: 9 → 11.

### L-1: XML doc says "SHA-256 of the tool result tag" (wrong)
**Decision: Auto-fix**
Fixed to "SHA-256 of the serialized tool input arguments" to match `ToolConventions.InputHash`
XML doc and the actual implementation.

### L-2: String comparison style inconsistency (`!=` vs `StringComparison.Ordinal`)
**Decision: Auto-fix**
Aligned to `string.Equals(operationName, ..., StringComparison.Ordinal)` matching
`ToolEffectivenessProcessor` and the rest of the codebase.

---

## Files Modified by Fixes
- `CausalSpanAttributionProcessor.cs` — XML doc, string comparison, two new comments
- `CausalSpanAttributionProcessorTests.cs` — 2 new iteration tests added
