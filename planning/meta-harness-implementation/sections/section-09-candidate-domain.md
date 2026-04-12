# Section 09: Candidate Domain

## Overview

This section implements the domain models and snapshot builder for the harness candidate system. These are pure value objects and one infrastructure service — no I/O other than the `ActiveConfigSnapshotBuilder` reading skill files and config.

**Dependencies:**
- section-01-config: `MetaHarnessConfig` (for `SnapshotConfigKeys`, `SecretsRedactionPatterns`)
- section-02-secret-redaction: `ISecretRedactor` (applied to all snapshot content)

**Blocks:** section-10-candidate-repository, section-11-proposer, section-12-evaluator, section-14-outer-loop

---

## New Files to Create

```
src/Content/Domain/Domain.Common/MetaHarness/HarnessSnapshot.cs
src/Content/Domain/Domain.Common/MetaHarness/SnapshotEntry.cs
src/Content/Domain/Domain.Common/MetaHarness/HarnessCandidate.cs
src/Content/Domain/Domain.Common/MetaHarness/HarnessCandidateStatus.cs
src/Content/Domain/Domain.Common/MetaHarness/EvalTask.cs
src/Content/Application/Application.AI.Common/Interfaces/MetaHarness/ISnapshotBuilder.cs
src/Content/Infrastructure/Infrastructure.AI/MetaHarness/ActiveConfigSnapshotBuilder.cs
src/Content/Tests/Application.AI.Common.Tests/MetaHarness/HarnessCandidateTests.cs
src/Content/Tests/Infrastructure.AI.Tests/MetaHarness/ActiveConfigSnapshotBuilderTests.cs
```

---

## Tests First

### Test file: `Application.AI.Common.Tests/MetaHarness/HarnessCandidateTests.cs`

Test project: `Application.AI.Common.Tests`

Tests cover the domain model's immutability contract and snapshot integrity. No mocks needed — these are pure value object tests.

```csharp
/// <summary>
/// Tests for HarnessCandidate domain model immutability and HarnessSnapshot integrity.
/// </summary>
public class HarnessCandidateTests
{
    // Test: HarnessCandidate_StatusTransition_ProducesNewImmutableRecord
    // Arrange: create a HarnessCandidate with Status=Proposed
    // Act: produce new record via `with { Status = HarnessCandidateStatus.Evaluated }`
    // Assert: original.Status == Proposed, updated.Status == Evaluated, !ReferenceEquals(original, updated)

    // Test: HarnessCandidate_WithExpression_DoesNotMutateOriginal
    // Arrange: create candidate, snapshot original BestScore
    // Act: create updated = candidate with { BestScore = 0.9, TokenCost = 1000 }
    // Assert: candidate.BestScore is still null; updated.BestScore == 0.9

    // Test: HarnessSnapshot_SnapshotManifest_ContainsHashForEachSkillFile
    // Arrange: build HarnessSnapshot with two skill file entries and a SnapshotManifest
    // Assert: SnapshotManifest.Count == 2, each entry has non-null/non-empty Sha256Hash
}
```

### Test file: `Infrastructure.AI.Tests/MetaHarness/ActiveConfigSnapshotBuilderTests.cs`

Test project: `Infrastructure.AI.Tests`

These tests use a temporary directory for skill files and a mock `ISecretRedactor`.

```csharp
/// <summary>
/// Tests for ActiveConfigSnapshotBuilder: secret exclusion, SHA256 hashing, and redaction.
/// </summary>
public class ActiveConfigSnapshotBuilderTests
{
    // Test: Build_ExcludesSecretKeys_FromConfigSnapshot
    // Arrange: config with keys ["ApiKey", "DatabaseName"]; patterns = ["Key"]
    // Act: Build(...)
    // Assert: snapshot.ConfigSnapshot does not contain "ApiKey"; does contain "DatabaseName"

    // Test: Build_IncludesAllowlistedConfigKeys
    // Arrange: SnapshotConfigKeys = ["DatabaseName", "Region"]
    // Act: Build(...)
    // Assert: both "DatabaseName" and "Region" present in ConfigSnapshot

    // Test: Build_ComputesSha256_ForEachSkillFile
    // Arrange: write two skill files to temp dir with known content
    // Act: Build(skillDirectory, ...)
    // Assert: SnapshotManifest has two entries; compute expected SHA256 inline and compare

    // Test: Build_AppliesRedactor_ToSystemPrompt
    // Arrange: mock ISecretRedactor.Redact(systemPrompt) returns "[REDACTED]"
    // Act: Build(...)
    // Assert: snapshot.SystemPromptSnapshot == "[REDACTED]"

    // Test: Build_SnapshotManifest_ContainsCorrectHashes
    // Arrange: single skill file with known UTF-8 content
    // Act: Build(...)
    // Assert: SnapshotManifest[0].Sha256Hash == expected SHA256 hex string for that content
}
```

---

## Domain Models

### `HarnessCandidateStatus` enum

**File:** `src/Content/Domain/Domain.Common/MetaHarness/HarnessCandidateStatus.cs`

```csharp
/// <summary>
/// Lifecycle states for a <see cref="HarnessCandidate"/> within an optimization run.
/// </summary>
public enum HarnessCandidateStatus
{
    /// <summary>The candidate has been proposed but not yet evaluated.</summary>
    Proposed,
    /// <summary>The candidate has been fully evaluated and scored.</summary>
    Evaluated,
    /// <summary>Evaluation or proposal failed; see <see cref="HarnessCandidate.FailureReason"/>.</summary>
    Failed,
    /// <summary>The candidate has been promoted to the active harness configuration.</summary>
    Promoted
}
```

### `SnapshotEntry` record

**File:** `src/Content/Domain/Domain.Common/MetaHarness/SnapshotEntry.cs`

```csharp
/// <summary>
/// An entry in a <see cref="HarnessSnapshot.SnapshotManifest"/> recording the SHA256
/// hash of a single skill file for reproducibility verification.
/// </summary>
public sealed record SnapshotEntry(
    /// <summary>Relative skill file path (e.g., "skills/research-agent/SKILL.md").</summary>
    string Path,
    /// <summary>Lowercase hex SHA256 hash of the file contents at snapshot time.</summary>
    string Sha256Hash);
```

### `HarnessSnapshot` record

**File:** `src/Content/Domain/Domain.Common/MetaHarness/HarnessSnapshot.cs`

Represents a deterministic, redacted point-in-time harness configuration.

```csharp
/// <summary>
/// Immutable, redacted snapshot of a harness configuration at a specific point in time.
/// Used to reproduce and compare candidate harness configurations during optimization.
/// </summary>
public sealed record HarnessSnapshot
{
    /// <summary>
    /// Skill file path → content for the active agent's skill directory only.
    /// Secrets have been removed via <see cref="ISecretRedactor"/>.
    /// </summary>
    public required IReadOnlyDictionary<string, string> SkillFileSnapshots { get; init; }

    /// <summary>
    /// System prompt at snapshot time, with secrets redacted.
    /// </summary>
    public required string SystemPromptSnapshot { get; init; }

    /// <summary>
    /// Selected AppConfig key/value pairs as declared in
    /// <see cref="MetaHarnessConfig.SnapshotConfigKeys"/>, minus any secret keys.
    /// </summary>
    public required IReadOnlyDictionary<string, string> ConfigSnapshot { get; init; }

    /// <summary>
    /// Per-file SHA256 hashes for all entries in <see cref="SkillFileSnapshots"/>.
    /// Enables verification that a snapshot can be faithfully reconstructed.
    /// </summary>
    public required IReadOnlyList<SnapshotEntry> SnapshotManifest { get; init; }
}
```

### `HarnessCandidate` record

**File:** `src/Content/Domain/Domain.Common/MetaHarness/HarnessCandidate.cs`

The core domain object. All mutations are expressed via C# `with` expressions — the original record is never modified.

```csharp
/// <summary>
/// Immutable domain record representing one proposed harness configuration within an
/// optimization run. Status transitions are performed via <c>with</c> expressions.
/// </summary>
public sealed record HarnessCandidate
{
    public required Guid CandidateId { get; init; }
    public required Guid OptimizationRunId { get; init; }

    /// <summary>Null for the seed candidate; set to the parent's <see cref="CandidateId"/> for all proposals.</summary>
    public Guid? ParentCandidateId { get; init; }

    public required int Iteration { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required HarnessSnapshot Snapshot { get; init; }

    /// <summary>Pass rate [0.0, 1.0] after evaluation. Null until evaluated.</summary>
    public double? BestScore { get; init; }

    /// <summary>Cumulative LLM token cost across all eval task runs. Null until evaluated.</summary>
    public long? TokenCost { get; init; }

    public required HarnessCandidateStatus Status { get; init; }

    /// <summary>Human-readable failure message. Only set when <see cref="Status"/> is <see cref="HarnessCandidateStatus.Failed"/>.</summary>
    public string? FailureReason { get; init; }
}
```

### `EvalTask` record

**File:** `src/Content/Domain/Domain.Common/MetaHarness/EvalTask.cs`

Loaded from JSON files under `MetaHarnessConfig.EvalTasksPath`. One file per task.

```csharp
/// <summary>
/// A single evaluation task used to score a <see cref="HarnessCandidate"/>.
/// Loaded from JSON files under <c>MetaHarnessConfig.EvalTasksPath</c>.
/// </summary>
public sealed record EvalTask
{
    /// <summary>Stable unique identifier for this task (used in trace paths).</summary>
    public required string TaskId { get; init; }

    /// <summary>Human-readable description of what the task exercises.</summary>
    public required string Description { get; init; }

    /// <summary>The prompt sent to the agent under evaluation.</summary>
    public required string InputPrompt { get; init; }

    /// <summary>
    /// Optional .NET regex pattern. Agent output must match for the task to pass.
    /// Null means the task is always considered passed (useful for smoke tests).
    /// </summary>
    public string? ExpectedOutputPattern { get; init; }

    /// <summary>Arbitrary tags for filtering (e.g., "smoke", "regression").</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}
```

---

## Application Interface

### `ISnapshotBuilder`

**File:** `src/Content/Application/Application.AI.Common/Interfaces/MetaHarness/ISnapshotBuilder.cs`

```csharp
/// <summary>
/// Builds a <see cref="HarnessSnapshot"/> from the currently active harness configuration.
/// Secrets are excluded and SHA256 hashes computed for all skill files.
/// </summary>
public interface ISnapshotBuilder
{
    /// <summary>
    /// Captures the active harness state into an immutable, redacted snapshot.
    /// </summary>
    /// <param name="skillDirectory">Absolute path to the agent's skill directory.</param>
    /// <param name="systemPrompt">Current system prompt text (will be redacted).</param>
    /// <param name="configValues">
    /// Key/value pairs from AppConfig to snapshot. Only keys in
    /// <see cref="MetaHarnessConfig.SnapshotConfigKeys"/> and not matching any
    /// <see cref="MetaHarnessConfig.SecretsRedactionPatterns"/> will be included.
    /// </param>
    /// <param name="cancellationToken"/>
    Task<HarnessSnapshot> BuildAsync(
        string skillDirectory,
        string systemPrompt,
        IReadOnlyDictionary<string, string> configValues,
        CancellationToken cancellationToken = default);
}
```

---

## Infrastructure Implementation

### `ActiveConfigSnapshotBuilder`

**File:** `src/Content/Infrastructure/Infrastructure.AI/MetaHarness/ActiveConfigSnapshotBuilder.cs`

Key behaviors:

1. **Config key filtering:** Include only keys present in `MetaHarnessConfig.SnapshotConfigKeys`. From those, exclude any whose name contains a substring from `MetaHarnessConfig.SecretsRedactionPatterns` (case-insensitive `string.Contains`).

2. **Skill file enumeration:** Enumerate all files directly under `skillDirectory` (and recursively). Read each file's content. Apply `ISecretRedactor.Redact` to the content before storing in `SkillFileSnapshots`.

3. **SHA256 hashing:** For each skill file, compute SHA256 over the raw UTF-8 bytes of the file content (before redaction — hash the source-of-truth). Store lowercase hex string in `SnapshotManifest`.

4. **System prompt redaction:** Pass `systemPrompt` through `ISecretRedactor.Redact` before storing.

```csharp
/// <summary>
/// Builds a <see cref="HarnessSnapshot"/> from the live filesystem and configuration.
/// Applies <see cref="ISecretRedactor"/> to all content before capture.
/// </summary>
public sealed class ActiveConfigSnapshotBuilder : ISnapshotBuilder
{
    private readonly MetaHarnessConfig _config;
    private readonly ISecretRedactor _redactor;

    public ActiveConfigSnapshotBuilder(
        IOptionsMonitor<MetaHarnessConfig> options,
        ISecretRedactor redactor)
    { /* assign fields */ }

    /// <inheritdoc/>
    public async Task<HarnessSnapshot> BuildAsync(
        string skillDirectory,
        string systemPrompt,
        IReadOnlyDictionary<string, string> configValues,
        CancellationToken cancellationToken = default)
    { /* implementation */ }

    private static string ComputeSha256Hex(byte[] bytes)
    { /* SHA256.HashData, convert to lowercase hex */ }

    private bool IsSecretKey(string key) =>
        _config.SecretsRedactionPatterns.Any(p =>
            key.Contains(p, StringComparison.OrdinalIgnoreCase));
}
```

**DI Registration** — add to `src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs`:

```csharp
services.AddSingleton<ISnapshotBuilder, ActiveConfigSnapshotBuilder>();
```

---

## Dependency Notes

- `ISecretRedactor` (from section-02) must be registered before `ActiveConfigSnapshotBuilder`.
- `MetaHarnessConfig` (from section-01) must be bound under `AppConfig.MetaHarness` before this section's DI is wired.
- The `Domain.Common/MetaHarness/` directory is new — no existing files need modification beyond adding the new files.
- `Application.AI.Common/Interfaces/MetaHarness/` directory is new — create it alongside the existing `Interfaces/` subdirectories.

---

## Implementation Notes (Actual vs Plan)

### Deviations from plan

1. **`ActiveConfigSnapshotBuilder` stores `IOptionsMonitor<T>` not snapshot** — plan stub showed `_config = options.CurrentValue` at construction. Changed to store `_options` reference and read `.CurrentValue` in `BuildAsync` so config reload works correctly for a singleton.

2. **Config key filtering delegates to `_redactor.IsSecretKey()`** — plan showed a private `IsSecretKey` method reading `_config.SecretsRedactionPatterns` directly. Changed to delegate to `_redactor.IsSecretKey(key)` (single source of truth; eliminates divergent denylist risk).

3. **SHA256 hash computed over post-redaction content** — plan said "hash the source-of-truth (before redaction)". After review discussion, changed to hash the redacted content so `SnapshotManifest` is self-consistent with `SkillFileSnapshots` for round-trip verification.

### Files created
| File | Notes |
|---|---|
| `Domain.Common/MetaHarness/HarnessCandidateStatus.cs` | As planned |
| `Domain.Common/MetaHarness/SnapshotEntry.cs` | As planned |
| `Domain.Common/MetaHarness/HarnessSnapshot.cs` | XML cref to `ISecretRedactor` replaced with `<c>` tag (Domain cannot reference Application) |
| `Domain.Common/MetaHarness/HarnessCandidate.cs` | As planned |
| `Domain.Common/MetaHarness/EvalTask.cs` | As planned |
| `Application.AI.Common/Interfaces/MetaHarness/ISnapshotBuilder.cs` | As planned |
| `Infrastructure.AI/MetaHarness/ActiveConfigSnapshotBuilder.cs` | Deviations 1-3 above |
| `Tests/Application.AI.Common.Tests/MetaHarness/HarnessCandidateTests.cs` | 3 tests, all pass |
| `Tests/Infrastructure.AI.Tests/MetaHarness/ActiveConfigSnapshotBuilderTests.cs` | 5 tests; added `IsSecretKey` mock setup after H-1 fix |

### Test results: 8/8 pass
