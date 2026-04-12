# Section 07: SKILL.md Extension

## Overview

Extend the skill parser and `SkillDefinition` type to recognize `## Objectives` and `## Trace Format` as first-class sections. Both sections are optional — existing skills without them continue to parse correctly. The proposer uses `Objectives` to understand what success looks like and `TraceFormat` to navigate the trace directory structure it reads during optimization.

This section has no dependencies on other sections. It can be implemented in Batch 1 (parallel with sections 01, 02, and 08).

## Background

The current skill system parses YAML frontmatter from SKILL.md files and treats everything after the closing `---` as the `Instructions` body (a flat markdown string). The parser lives in `Infrastructure.AI/Skills/SkillMetadataParser.cs` and populates `Domain.AI/Skills/SkillDefinition.cs`.

The harness proposer needs two structured pieces of information that are logically distinct from the procedural instructions:

- **Objectives** — success criteria, failure patterns, and trade-offs for the agent. This is what the proposer optimizes _toward_. It should be a first-class property rather than buried in the instructions body.
- **TraceFormat** — documentation of the trace directory layout. The proposer reads execution traces to understand what went wrong; it needs a map of where things live.

Making these first-class (rather than convention-based subsections of `Instructions`) allows `ISkillMetadataRegistry` to surface them without loading the full instructions body, and allows the evaluator and proposer to query them by name.

## Files to Create

### `skills/harness-proposer/SKILL.md`

New skill file at the repo root. The harness proposer is the meta-agent that reads execution traces and proposes skill/prompt changes. Its SKILL.md is itself a candidate for optimization by the outer loop.

```
skills/
  harness-proposer/
    SKILL.md    ← create this
  research-agent/
    SKILL.md    ← update this (add ## Objectives and ## Trace Format)
```

The `harness-proposer/SKILL.md` must include valid YAML frontmatter plus both `## Objectives` and `## Trace Format` sections in its body.

Frontmatter fields required:
- `name: "harness-proposer"`
- `description: "Reads execution traces and proposes skill/prompt changes to improve agent performance."`
- `category: "meta"`
- `skill_type: "orchestration"`
- `version: "1.0.0"`
- `tags: ["meta", "optimization", "harness"]`
- `allowed-tools: ["file_system", "read_history"]`

`## Objectives` section content should document: improving pass rate on eval tasks, reducing token cost per task, identifying failure patterns from traces, proposing targeted changes to skill instructions or system prompts.

`## Trace Format` section content should document: the trace directory structure as written by `FileSystemExecutionTraceStore` — execution run directories, `traces.jsonl`, `decisions.jsonl`, `manifest.json`, and the `candidates/` directory layout.

## Files to Modify

### `Domain.AI/Skills/SkillDefinition.cs`

Add two new optional properties in the Level 2 region (they are loaded on demand alongside `Instructions`):

```csharp
/// <summary>
/// Structured objectives extracted from the ## Objectives section of SKILL.md.
/// Surfaces success criteria, failure patterns, and trade-offs for the agent.
/// Null when the section is absent (backward compatible).
/// </summary>
public string? Objectives { get; set; }

/// <summary>
/// Trace directory layout documentation extracted from the ## Trace Format section of SKILL.md.
/// Used by the harness proposer to navigate execution trace directories.
/// Null when the section is absent (backward compatible).
/// </summary>
public string? TraceFormat { get; set; }
```

Add computed properties alongside the existing `HasTemplates` pattern:

```csharp
public bool HasObjectives => !string.IsNullOrWhiteSpace(Objectives);
public bool HasTraceFormat => !string.IsNullOrWhiteSpace(TraceFormat);
```

### `Infrastructure.AI/Skills/SkillMetadataParser.cs`

Extend `ParseFromFile` and `Parse` to extract `## Objectives` and `## Trace Format` sections from the body string. The extraction logic should be a private helper:

```csharp
/// <summary>
/// Extracts the content of a named ## Heading section from a markdown body.
/// Returns null if the heading is not present. Content ends at the next ## heading or EOF.
/// </summary>
private static string? ExtractSection(string body, string heading);
```

After extracting both sections, strip them from `Instructions` so the instructions body does not duplicate content already surfaced as structured properties. Both methods (`ParseFromFile` and `Parse`) set `Objectives` and `TraceFormat` on the returned `SkillDefinition`.

The heading match should be case-insensitive and trim the extracted content.

### `Application.AI.Common/Interfaces/ISkillMetadataRegistry.cs`

No interface signature changes are needed — `GetAll()` and `TryGet()` already return `SkillDefinition`, which will now carry the new properties. However, add a `GetBySkillType` convenience method if not already present (the proposer queries by `skill_type: "orchestration"`):

```csharp
/// <summary>
/// Returns skills matching the given skill type (e.g., "orchestration", "analysis").
/// </summary>
IReadOnlyList<SkillDefinition> GetBySkillType(string skillType);
```

If `GetBySkillType` already exists on the concrete registry but not the interface, add it to the interface now to avoid the proposer casting to the concrete type.

## Files to Modify: `skills/research-agent/SKILL.md`

Add both sections to the existing file after the current `## Guidelines` section:

```markdown
## Objectives

- Locate the specific information requested — files, classes, methods, config values, or patterns
- Return exact file paths and line numbers where applicable
- Identify uncertainty explicitly rather than speculating
- Minimize tool calls: prefer targeted searches over broad directory listings

## Trace Format

Not applicable — this skill does not produce structured traces. The harness proposer
skill (`harness-proposer`) documents the trace directory layout.
```

## Tests to Write

**Test project:** `Application.AI.Common.Tests` (for parser tests using `SkillMetadataParser` directly)

Test file: `src/Content/Tests/Application.AI.Common.Tests/Skills/SkillParserExtensionTests.cs`

### Test stubs

```csharp
public sealed class SkillParserExtensionTests
{
    /// <summary>Verify ## Objectives content is extracted into SkillDefinition.Objectives.</summary>
    [Fact]
    public void SkillParser_WithObjectivesSection_ExtractsObjectivesContent() { }

    /// <summary>Verify ## Trace Format content is extracted into SkillDefinition.TraceFormat.</summary>
    [Fact]
    public void SkillParser_WithTraceFormatSection_ExtractsTraceFormatContent() { }

    /// <summary>Verify Skills without ## Objectives produce null Objectives (not empty string).</summary>
    [Fact]
    public void SkillParser_WithoutObjectivesSection_ReturnsNullObjectives() { }

    /// <summary>Verify Skills without ## Trace Format produce null TraceFormat (not empty string).</summary>
    [Fact]
    public void SkillParser_WithoutTraceFormatSection_ReturnsNullTraceFormat() { }

    /// <summary>Verify Instructions body does not contain the ## Objectives or ## Trace Format content after extraction.</summary>
    [Fact]
    public void SkillParser_ExtractedSections_AreRemovedFromInstructions() { }
}
```

**Test project:** `Infrastructure.AI.Tests` (for registry integration using real SKILL.md files)

Add to existing `SkillMetadataRegistryTests.cs`:

```csharp
/// <summary>SkillDefinition returned by registry includes Objectives when SKILL.md has ## Objectives.</summary>
[Fact]
public void SkillMetadataRegistry_IncludesObjectives_InReturnedSkillDefinition() { }

/// <summary>Existing skills without new sections still parse without error (no regression).</summary>
[Fact]
public void SkillMetadataRegistry_ExistingSkillsWithoutNewSections_ParseCorrectly() { }
```

### Test data pattern

The parser tests should construct `SkillMetadataParser` directly with `NullLogger` (same pattern as `SkillMetadataRegistryTests`) and call `ParseFromFile` against a temp file, or `Parse` with inline string content. Use inline SKILL.md content strings rather than fixture files so tests are self-contained:

```csharp
const string skillMd = """
    ---
    name: "test-skill"
    description: "A test skill"
    ---

    ## Instructions

    Do the thing.

    ## Objectives

    - Succeed at the thing.

    ## Trace Format

    Traces live under traces/{run_id}/.
    """;
```

Call `parser.Parse("test-skill", "A test skill", body, tempDir)` where `body` is the content after the frontmatter closing `---`.

## Implementation Notes

- The `ExtractSection` helper should handle edge cases: heading at EOF (no following `##`), multiple sections of the same name (use first occurrence), heading with trailing whitespace or varying case (`## Objectives`, `## objectives`, `## OBJECTIVES` all match).
- The `Instructions` property after extraction should contain the markdown body with the extracted sections removed, with surrounding whitespace normalized (no double blank lines at extraction points). This keeps the Instructions token count accurate.
- Do not add `Objectives` or `TraceFormat` to the Level 1 token estimate — they are Level 2 content loaded on demand.
- The `harness-proposer` skill's `## Trace Format` section is the authoritative reference for the trace directory layout (documented in section 04). Write it accurately against the `FileSystemExecutionTraceStore` output structure, even though section 04 is not yet implemented when this section runs. Use the plan's description as the source of truth.
- `SkillMetadataRegistry` implementation (`Infrastructure.AI/Skills/SkillMetadataRegistry.cs`) calls `SkillMetadataParser.ParseFromFile` — no registry changes are needed beyond adding `GetBySkillType` to the interface if missing.

## Implementation Notes (Actual)

### Deviations from Plan
- **Test project**: Parser tests placed in `Infrastructure.AI.Tests` (not `Application.AI.Common.Tests` as specified). `Application.AI.Common.Tests` has no `Infrastructure.AI` reference — moving tests would require adding a cross-layer project reference.
- **Code fence fix added**: `ExtractSection` and `StripSections` both track `inFence` state to ignore `## Headings` inside triple-backtick code blocks. This was a review finding (H-1) and implemented immediately.
- **M-4 refactor**: Extracted `ExtractStructuredSections(string body)` private helper returning `(Objectives, TraceFormat, Instructions)` tuple, called from both `ParseFromFile` and `Parse`.
- **Level2TokenEstimate**: Updated to include `Objectives` and `TraceFormat` tokens so `IsLevel2Oversized` remains accurate.
- **GetBySkillType null guard**: `ArgumentException.ThrowIfNullOrWhiteSpace` added before `EnsureLoaded()`.

### Files Actually Created/Modified
- `src/Content/Domain/Domain.AI/Skills/SkillDefinition.cs` — `Objectives`, `TraceFormat`, `HasObjectives`, `HasTraceFormat`, updated `Level2TokenEstimate`
- `src/Content/Infrastructure/Infrastructure.AI/Skills/SkillMetadataParser.cs` — `ExtractStructuredSections`, `ExtractSection` (fence-aware), `StripSections` (fence-aware)
- `src/Content/Application/Application.AI.Common/Interfaces/ISkillMetadataRegistry.cs` — `GetBySkillType`
- `src/Content/Infrastructure/Infrastructure.AI/Skills/SkillMetadataRegistry.cs` — `GetBySkillType` with null guard
- `src/Content/Tests/Infrastructure.AI.Tests/Skills/SkillParserExtensionTests.cs` — 5 unit tests (new)
- `src/Content/Tests/Infrastructure.AI.Tests/Skills/SkillMetadataRegistryTests.cs` — 2 integration tests added
- `skills/harness-proposer/SKILL.md` — new skill file
- `skills/research-agent/SKILL.md` — `## Objectives` and `## Trace Format` sections added

### Final Test Count
- 5 new parser unit tests (all pass)
- 2 new registry integration tests (all pass)
- 254 total in Infrastructure.AI.Tests, 0 failures

## Dependency Notes

- **No dependencies** — this section is self-contained and runs in Batch 1.
- **Blocks section 11 (Proposer)** — `OrchestratedHarnessProposer` reads `Objectives` and `TraceFormat` from the proposer's skill definition.
- Section 08 (`ISkillContentProvider`) runs in parallel and uses `SkillDefinition` but does not depend on the new properties.
