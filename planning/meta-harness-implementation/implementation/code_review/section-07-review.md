# Code Review: Section-07 Skill Extension (Objectives + Trace Format)

## Summary

Clean extension of the SKILL.md parser to extract `## Objectives` and `## Trace Format` as first-class properties on `SkillDefinition`. The approach is backward-compatible (null when absent), the extraction/stripping logic is well-structured, and the test coverage hits the main paths. No security issues. The primary concern is that the markdown parser does not respect code fences, which creates a latent correctness bug. There is also a token budget tracking gap where the new properties are excluded from `Level2TokenEstimate`.

## Verdict: WARNING -- merge with fixes for H-1 and M-1

## Findings

### CRITICAL

None.

### HIGH

**[H-1] ExtractSection/StripSections do not respect markdown code fences**
File: `SkillMetadataParser.cs:144-223`

Both methods scan for lines starting with `## ` to detect section boundaries. A `## Heading` inside a fenced code block (triple backticks) will be incorrectly treated as a real heading, either terminating extraction early or splitting a section incorrectly.

This is not theoretical -- `harness-proposer/SKILL.md:31` has `## Proposal` inside a code fence. It is currently safe only by accident (it appears before `## Objectives`, not between extracted sections). Any future SKILL.md with a fenced `## ` line between `## Objectives` and the next real heading will silently truncate content.

Fix -- track fence state in both methods. Add a `var inFence = false;` flag, toggle it when a line starts with triple backticks, and skip heading detection while `inFence` is true. Apply the same pattern in both `ExtractSection` and `StripSections`. Add a test with a code-fenced `## ` heading inside a section to prove correctness.

### MEDIUM

**[M-1] Level2TokenEstimate excludes Objectives and TraceFormat tokens**
File: `SkillDefinition.cs:278`

`Objectives` and `TraceFormat` are Level 2 properties (they live in the `#region Level 2` block and are loaded on demand from the SKILL.md body). But the token estimate only counts `Instructions`. When the harness injects these properties into the agent context, the actual token usage will exceed the estimate, potentially blowing the 5,000-token budget without `IsLevel2Oversized` catching it.

Fix:

```csharp
public int Level2TokenEstimate =>
    EstimateTokens(Instructions ?? string.Empty) +
    EstimateTokens(Objectives ?? string.Empty) +
    EstimateTokens(TraceFormat ?? string.Empty);
```

**[M-2] GetBySkillType does not guard against null parameter**
File: `SkillMetadataRegistry.cs:82`

`string.Equals(s.SkillType, skillType, ...)` with a null `skillType` will match all skills that also have a null `SkillType`, returning unexpected results. The sibling methods (`GetByCategory`, `GetByTags`) have the same pattern, so this is a pre-existing gap, but since `GetBySkillType` is new code it should set the precedent.

Fix -- add an `ArgumentException.ThrowIfNullOrWhiteSpace(skillType)` guard, or document the null-matching behavior as intentional.

**[M-3] Integration tests silently pass when skills directory is absent**
File: `SkillMetadataRegistryTests.cs:149,163`

```csharp
if (!Directory.Exists(SkillsPath))
    return;
```

This is a pre-existing pattern in the test file, but the two new tests inherit it. In a CI environment where skills/ doesn't exist, these tests report as "passed" (green) without actually running. This masks regressions.

Fix -- use `Assert.Skip("Skills directory not found")` (xUnit v2.8+) so the test runner reports them as skipped, not passed.

**[M-4] Duplicate extraction logic across ParseFromFile and Parse**
File: `SkillMetadataParser.cs:36-38,90-92`

The three-line block appears identically in both methods:

```csharp
var objectives = ExtractSection(body, "Objectives");
var traceFormat = ExtractSection(body, "Trace Format");
var instructions = StripSections(body, "Objectives", "Trace Format");
```

If a third section is added later, both call sites must be updated. Consider extracting a small helper that returns a tuple. Not blocking -- three lines is manageable -- but will compound as more sections are added.

### LOW

**[L-1] HasObjectives / HasTraceFormat lack XML docs**
File: `SkillDefinition.cs:236-237`

Per project rules (XML docs on all public types -- this is a template), the new computed properties should include `<summary>` tags. Pre-existing gap in the other computed properties, but new code should set the standard.

**[L-2] No test coverage for ParseFromFile path with Objectives/TraceFormat**
File: `SkillParserExtensionTests.cs`

All 5 unit tests call `parser.Parse(...)`, which takes a pre-extracted body. The `ParseFromFile` method (which reads from disk and extracts the body itself) is only covered by the integration tests, which silently skip in some environments (see M-3). Consider adding one unit test that writes a temp SKILL.md with frontmatter + sections and calls `ParseFromFile` directly.

**[L-3] StripSections uses `params string[]` -- consider `params ReadOnlySpan<string>` (.NET 10)**
File: `SkillMetadataParser.cs:180`

Since the project targets .NET 10, `params ReadOnlySpan<string>` avoids the implicit array allocation. Minor perf -- the method is called twice per skill parse, not in a hot path.

### INFO

**[I-1] harness-proposer/SKILL.md is well-structured**
The new skill file demonstrates the correct pattern for future skill authors: frontmatter, preamble, `## Instructions` with subsections, `## Objectives`, `## Trace Format`. Good reference material.

**[I-2] research-agent/SKILL.md Trace Format content**
The "Not applicable" text for Trace Format parses to a non-null string, meaning `HasTraceFormat` returns true even though the skill doesn't produce traces. If this distinction matters downstream, consider returning null for "Not applicable" sections. Currently harmless.

**[I-3] GetBySkillType is declared but has no callers yet**
File: `ISkillMetadataRegistry.cs:39`
Expected for a section building toward the outer loop (sections 11-14). Noted for completeness.

## Files Reviewed

| File | Lines | Status |
|------|-------|--------|
| `SkillDefinition.cs` | 311 | OK (under 400 limit) |
| `SkillMetadataParser.cs` | 269 | OK (under 400 limit) |
| `SkillMetadataRegistry.cs` | 189 | OK |
| `ISkillMetadataRegistry.cs` | 46 | OK |
| `SkillParserExtensionTests.cs` | 141 | OK |
| `SkillMetadataRegistryTests.cs` | 185 | OK |
| `harness-proposer/SKILL.md` | 115 | OK |
| `research-agent/SKILL.md` | +12 lines | OK |

## Test Coverage Assessment

- **ExtractSection present/absent**: Covered (4 tests)
- **StripSections removes content from Instructions**: Covered (1 test)
- **Registry round-trip with Objectives**: Covered (1 integration test)
- **Backward compat (no new sections)**: Covered (1 integration test)
- **Gaps**: No ParseFromFile unit test, no code-fence edge case test, no empty-section test (heading present, no content below)
