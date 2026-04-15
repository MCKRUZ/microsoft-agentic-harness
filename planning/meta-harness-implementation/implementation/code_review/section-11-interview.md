# Section 11 — Code Review Interview

## Findings Disposition

### H-1 — Iteration indexing inconsistency
**Finding:** `HarnessCandidate.Iteration` is zero-based per its XML doc, but `HarnessProposerContext.Iteration` was documented as 1-based.  
**Decision:** Standardize on zero-based (user selected). Update `HarnessProposerContext.Iteration` doc comment to say "zero-based".  
**Fix:** Update doc comment in `HarnessProposerContext.cs`.

### M-1 — Fragile JSON extraction
**Finding:** `IndexOf('{')` / `LastIndexOf('}')` can span unrelated brace pairs in preamble text.  
**Decision:** Let go — this is the exact extraction algorithm specified in the section spec. The SKILL.md instructs the agent to emit no preamble. Noted as a known limitation.

### M-2 — RawOutput may leak sensitive data via structured logging  
**Finding:** `HarnessProposalParsingException.RawOutput` stores full unbounded agent output; structured loggers serialize exception properties.  
**Decision:** Auto-fix. Truncate stored `RawOutput` to 500 chars with `"…[truncated]"` suffix. The exception message already only includes output length (safe).

### L-1 — No input validation on context parameter
**Decision:** Let go. Internal call path — validate at system boundaries only (project convention).

### L-2 — AvailableAgents populated with tool names
**Finding:** `AvailableAgents` is documented as sub-agent names but receives tool names ("file_system", "read_history").  
**Decision:** Keep as-is for now (user selected). The outer loop / handler doesn't enforce the distinction yet. Revisit in section-14 outer loop.

### L-3 — Test coverage gaps
**Decision:** Let go. POC coverage (4 tests covering key paths) is sufficient. Full edge-case coverage is out of scope for this section.

## Applied Fixes

1. `HarnessProposerContext.cs` — doc comment: "1-based" → "zero-based"
2. `HarnessProposalParsingException.cs` — truncate `RawOutput` to 500 chars
