# Code Review Interview — Section 07: SKILL.md Extension

## Findings Triage

### H-1: Code fence handling (ASK USER → Fix)
**User decision:** Fix now — add inFence tracking to both `ExtractSection` and `StripSections`.
**Auto-fix:** Add `inFence` bool toggle in both methods; skip heading detection when inside triple backtick fences.

### M-1: Level2TokenEstimate incomplete (AUTO-FIX)
`Level2TokenEstimate` only counted `Instructions`. Adding `Objectives` and `TraceFormat` to the estimate so `IsLevel2Oversized` reflects the true Level 2 budget.

### M-2: GetBySkillType null guard (AUTO-FIX)
Add `ArgumentException.ThrowIfNullOrWhiteSpace(skillType)` at entry. Prevents null from silently matching skills with null `SkillType`.

### M-3: Assert.Skip vs return in registry tests (LET GO)
Minor style difference; not a correctness issue. The `return` pattern is already used consistently across the existing test file.

### M-4: Duplicate extraction block (AUTO-FIX)
Extract a `ExtractStructuredSections(string body)` helper returning a tuple `(Objectives, TraceFormat, Instructions)` to prevent future divergence between `ParseFromFile` and `Parse`.

### LOW: XML docs on new computed properties (AUTO-FIX)
Add XML docs to `HasObjectives` and `HasTraceFormat`.

### LOW: params ReadOnlySpan (LET GO)
Micro-optimization; not worth changing public helper signature now.

### LOW: ParseFromFile unit test (LET GO)
The `ExtractSection`/`StripSections` logic is shared — the Parse-path tests cover the same code paths. A separate ParseFromFile test would just test file I/O wiring, which SkillMetadataRegistryTests already covers via the integration tests.

## Fixes Applied

1. **H-1** — Added `inFence` tracking to `ExtractSection` and `StripSections`
2. **M-1** — Updated `Level2TokenEstimate` to include `Objectives` and `TraceFormat`
3. **M-2** — Added `ArgumentException.ThrowIfNullOrWhiteSpace` to `GetBySkillType`
4. **M-4** — Extracted `ExtractStructuredSections` private helper
5. **LOW** — Added XML docs to `HasObjectives` and `HasTraceFormat`
