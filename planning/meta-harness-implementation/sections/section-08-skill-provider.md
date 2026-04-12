# Section 08: Skill Provider (`ISkillContentProvider`)

## Overview

This section implements candidate isolation for the evaluation pipeline. When the evaluator tests a proposed harness candidate, it must use that candidate's skill file content — not what's currently on disk. `ISkillContentProvider` is the seam that makes this possible without touching the filesystem during evaluation.

This section has no dependencies on other sections and can be implemented in parallel with sections 01–07. It is a prerequisite for section 11 (Proposer) and section 12 (Evaluator).

---

## Background

The meta-harness optimization loop proposes changes to skill files as part of each `HarnessProposal`. Those proposed changes are stored in-memory in a `HarnessCandidate.Snapshot.SkillFileSnapshots` dictionary (implemented in section 09). When the evaluator runs an agent against a candidate, it must inject that in-memory content instead of reading from `skills/` on disk — otherwise all candidates would evaluate identically against the current on-disk state.

`AgentExecutionContextFactory` (already in `Application.AI.Common/Factories/`) is the construction site for agent contexts. This section adds an optional `ISkillContentProvider` override parameter to that factory so the evaluator can pass a candidate-scoped provider.

---

## Files to Create

### 1. Interface

**`src/Content/Application/Application.AI.Common/Interfaces/Skills/ISkillContentProvider.cs`**

```csharp
/// <summary>
/// Provides skill file content by path. Abstraction over filesystem and in-memory sources.
/// Implementors return null when the requested path is not available from their source,
/// allowing callers to fall back to alternative providers.
/// </summary>
public interface ISkillContentProvider
{
    /// <summary>
    /// Returns the content of the skill file at <paramref name="skillPath"/>,
    /// or null if this provider does not have content for that path.
    /// </summary>
    Task<string?> GetSkillContentAsync(string skillPath, CancellationToken cancellationToken = default);
}
```

### 2. Filesystem Implementation

**`src/Content/Infrastructure/Infrastructure.AI/Skills/FileSystemSkillContentProvider.cs`**

Reads skill file content from disk. Returns `null` (not an exception) when the file does not exist. This is the default implementation registered in DI for normal (non-optimization) agent runs.

Stub:
```csharp
/// <summary>
/// Reads skill content from the local filesystem.
/// Returns null when the file does not exist.
/// </summary>
public sealed class FileSystemSkillContentProvider : ISkillContentProvider
{
    public Task<string?> GetSkillContentAsync(string skillPath, CancellationToken cancellationToken = default);
}
```

### 3. Candidate (In-Memory) Implementation

**`src/Content/Infrastructure/Infrastructure.AI/Skills/CandidateSkillContentProvider.cs`**

Reads from a `IReadOnlyDictionary<string, string>` snapshot (skill path → content) supplied at construction. Returns `null` for any path not present in the snapshot. Does not touch the filesystem.

```csharp
/// <summary>
/// Serves skill content from an in-memory snapshot of a <see cref="HarnessCandidate"/>.
/// Used during evaluation to isolate candidate skill content from the active filesystem state.
/// Returns null for paths not present in the snapshot.
/// </summary>
public sealed class CandidateSkillContentProvider : ISkillContentProvider
{
    private readonly IReadOnlyDictionary<string, string> _skillFileSnapshots;

    public CandidateSkillContentProvider(IReadOnlyDictionary<string, string> skillFileSnapshots);

    public Task<string?> GetSkillContentAsync(string skillPath, CancellationToken cancellationToken = default);
}
```

The constructor parameter is `IReadOnlyDictionary<string, string>` — it receives `candidate.Snapshot.SkillFileSnapshots` directly. No reference to the full `HarnessCandidate` type is needed here, which keeps this class independent of section 09.

---

## Files to Modify

### `AgentExecutionContextFactory`

**`src/Content/Application/Application.AI.Common/Factories/AgentExecutionContextFactory.cs`**

Add an optional `ISkillContentProvider` parameter to the context-creation method (or constructor — check the existing signature and match its pattern). When the override is provided, use it. When null, resolve the registered `ISkillContentProvider` from the DI container (which will be `FileSystemSkillContentProvider`).

The exact method signature to extend depends on the existing factory interface — read the file before editing. The intent is: callers constructing eval contexts pass a `CandidateSkillContentProvider`; callers constructing normal agent runs pass `null` (or omit it).

### `Infrastructure.AI/DependencyInjection.cs`

Register `FileSystemSkillContentProvider` as the default `ISkillContentProvider` (singleton or transient — transient preferred since it's stateless and cheap):

```csharp
services.AddTransient<ISkillContentProvider, FileSystemSkillContentProvider>();
```

Do not register `CandidateSkillContentProvider` — it is constructed directly by `AgentEvaluationService` (section 12) with the candidate's snapshot data.

---

## Tests to Write

**Test project:** `src/Content/Tests/Application.AI.Common.Tests/` and/or `src/Content/Tests/Infrastructure.AI.Tests/`

Create the test file at: `src/Content/Tests/Infrastructure.AI.Tests/Skills/SkillContentProviderTests.cs`

### `CandidateSkillContentProvider` Tests

```
GetSkillContentAsync_PathInSnapshot_ReturnsSnapshotContent
  Arrange: snapshot with {"skills/foo/SKILL.md" → "# Foo content"}
  Act: GetSkillContentAsync("skills/foo/SKILL.md")
  Assert: returns "# Foo content"

GetSkillContentAsync_PathNotInSnapshot_ReturnsNull
  Arrange: snapshot with {"skills/foo/SKILL.md" → "..."}
  Act: GetSkillContentAsync("skills/bar/SKILL.md")
  Assert: returns null
```

### `FileSystemSkillContentProvider` Tests

```
GetSkillContentAsync_ExistingFile_ReturnsContent
  Arrange: write a temp file with known content
  Act: GetSkillContentAsync(tempFilePath)
  Assert: returns the file content

GetSkillContentAsync_NonExistentFile_ReturnsNull
  Arrange: path that does not exist on disk
  Act: GetSkillContentAsync(nonExistentPath)
  Assert: returns null (no exception thrown)
```

All four tests are straightforward — no mocks needed. Use `Path.GetTempFileName()` for filesystem tests and clean up in `IDisposable.Dispose`.

---

## Implementation Notes (Actual)

### Files Created/Modified
- `src/Content/Application/Application.AI.Common/Interfaces/Skills/ISkillContentProvider.cs` — interface + `AdditionalPropertiesKey` constant
- `src/Content/Infrastructure/Infrastructure.AI/Skills/FileSystemSkillContentProvider.cs` — try/catch instead of File.Exists (TOCTOU fix); trust boundary doc comment
- `src/Content/Infrastructure/Infrastructure.AI/Skills/CandidateSkillContentProvider.cs` — constructor normalizes to `OrdinalIgnoreCase` dictionary; `ArgumentNullException.ThrowIfNull`
- `src/Content/Application/Application.AI.Common/Factories/AgentExecutionContextFactory.cs` — optional `ISkillContentProvider?` constructor param; stored in `AdditionalProperties[AdditionalPropertiesKey]`
- `src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs` — `AddTransient<ISkillContentProvider, FileSystemSkillContentProvider>()`
- `src/Content/Tests/Infrastructure.AI.Tests/Skills/SkillContentProviderTests.cs` — 4 tests (new)

### Deviations from Plan
- `CandidateSkillContentProvider` normalizes the incoming dictionary to `OrdinalIgnoreCase` at construction rather than accepting it as-is. This handles Windows path casing differences between snapshot keys and runtime lookup values.
- `FileSystemSkillContentProvider` uses try/catch instead of `File.Exists` check to eliminate TOCTOU race.
- `ISkillContentProvider.AdditionalPropertiesKey` constant added to interface for type-safe key access.

### Final Test Count
- 4 new unit tests, all passing
- 924 total across solution, 0 failures

## Dependency Notes

- **Depends on:** Nothing. This section has no upstream dependencies and can be implemented immediately.
- **Required by section 11 (Proposer):** `AgentExecutionContextFactory` must accept `ISkillContentProvider` override before the evaluator can inject candidate content.
- **Required by section 12 (Evaluator):** `AgentEvaluationService` constructs a `CandidateSkillContentProvider` from `candidate.Snapshot.SkillFileSnapshots` and passes it to the factory. The dictionary type used in `CandidateSkillContentProvider`'s constructor must match the type defined in section 09's `HarnessSnapshot`. Since section 09 defines `SkillFileSnapshots` as `IReadOnlyDictionary<string, string>`, this constructor should accept the same type.
- **Section 09** (`HarnessSnapshot`) defines the dictionary this provider reads from. You can stub `CandidateSkillContentProvider` against a plain `IReadOnlyDictionary<string, string>` now and it will wire up to `HarnessSnapshot.SkillFileSnapshots` in section 12 without changes.
