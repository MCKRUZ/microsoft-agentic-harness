# Code Review: Section-09 Candidate Domain Models + Snapshot Builder

## Summary

Five domain records/enums defining the meta-harness candidate model, one Application-layer interface (ISnapshotBuilder), one Infrastructure implementation (ActiveConfigSnapshotBuilder), two test files (8 tests), and a DI registration update. The domain models are well-structured sealed records with init-only properties. The snapshot builder reads skill files from disk, applies ISecretRedactor.Redact() to content and system prompt, filters config keys by allowlist + denylist, and computes SHA256 hashes over raw bytes before redaction.

The primary concerns are: (1) the snapshot builder duplicates IsSecretKey logic that already exists on ISecretRedactor, (2) skillDirectory is not validated against path traversal, (3) the builder is registered as Singleton but captures IOptionsMonitor.CurrentValue at construction time making config reload a no-op, and (4) the SHA256 hash computed over pre-redaction content creates a verification mismatch since the snapshot stores post-redaction content.

## Verdict: WARNING -- merge with fixes for H-1 and M-1 through M-3


---

## Detailed Findings

### H-1 | HIGH | Snapshot builder duplicates IsSecretKey logic from ISecretRedactor

**File:** `src/Content/Infrastructure/Infrastructure.AI/MetaHarness/ActiveConfigSnapshotBuilder.cs:101-103`

**Problem:**
`ActiveConfigSnapshotBuilder.IsSecretKey()` re-implements the same case-insensitive substring matching that `ISecretRedactor.IsSecretKey(string configKey)` already defines (see `Application.AI.Common/Interfaces/ISecretRedactor.cs:49`). The builder reads its patterns from `MetaHarnessConfig.SecretsRedactionPatterns`, while the `ISecretRedactor` implementation reads from wherever its own implementation is configured. If the two pattern sources diverge -- for example, a new pattern added to the redactor but not to `MetaHarnessConfig` -- the builder will leak secrets that the redactor would have caught, or vice versa.

This violates DRY and creates a security-relevant divergence risk: two independent codepaths deciding "is this a secret?" with potentially different answers.

**Recommended fix:**
Delete the private `IsSecretKey` method and delegate to the injected `_redactor`:

```csharp
// Before (lines 85-86, 101-103):
if (IsSecretKey(key))
    continue;

private bool IsSecretKey(string key) =>
    _config.SecretsRedactionPatterns.Any(p =>
        key.Contains(p, StringComparison.OrdinalIgnoreCase));

// After:
if (_redactor.IsSecretKey(key))
    continue;

// Delete IsSecretKey entirely -- single source of truth is ISecretRedactor.
```

If `MetaHarnessConfig.SecretsRedactionPatterns` becomes unused after this change, either remove it or have the `ISecretRedactor` implementation consume it -- but only one component should own the denylist.

**Blocker:** No, but should fix before merging to avoid a future secret-leak divergence.

---

### M-1 | MEDIUM | skillDirectory not validated against path traversal

**File:** `src/Content/Infrastructure/Infrastructure.AI/MetaHarness/ActiveConfigSnapshotBuilder.cs:38, 58-76`

**Problem:**
`BuildAsync` accepts a `string skillDirectory` parameter and passes it directly to `Directory.EnumerateFiles` (line 67) with `SearchOption.AllDirectories`. There is no validation that the path:
1. Is an absolute, canonical path (no `..` segments)
2. Falls within an expected base directory
3. Does not resolve to a sensitive location (e.g., user SSH keys, `/etc/`)

If the caller passes a user-influenced value like `"../../../etc"` or `"skills/../../../../secrets"`, the builder will read and snapshot every file under that directory tree. The content is then stored in `HarnessSnapshot.SkillFileSnapshots`, potentially exposing sensitive files.

**Recommended fix:**
Validate and canonicalize the path before enumeration:

```csharp
private static async Task<Dictionary<string, string>> EnumerateSkillFilesAsync(
    string skillDirectory,
    CancellationToken cancellationToken)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    var fullPath = Path.GetFullPath(skillDirectory);

    if (!Directory.Exists(fullPath))
        return result;

    foreach (var filePath in Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories))
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Guard: ensure resolved file is still under the skill directory
        var resolvedFile = Path.GetFullPath(filePath);
        if (!resolvedFile.StartsWith(fullPath, StringComparison.OrdinalIgnoreCase))
            continue;

        var relativePath = Path.GetRelativePath(fullPath, resolvedFile)
            .Replace('\\', '/');
        result[relativePath] = await File.ReadAllTextAsync(
            resolvedFile, Encoding.UTF8, cancellationToken);
    }

    return result;
}
```

Alternatively, if the project already has `FileSystemConfig.AllowedBasePaths` validation (referenced in `MetaHarnessConfig` XML docs at `Domain.Common/Config/MetaHarness/MetaHarnessConfig.cs:103`), inject `IFileSystemService` and use its path validation rather than rolling a new one.

**Blocker:** No, but should fix -- path traversal is a well-known attack vector and this is a file-reading operation.

---

### M-2 | MEDIUM | Singleton captures IOptionsMonitor.CurrentValue at construction, defeating config reload

**File:** `src/Content/Infrastructure/Infrastructure.AI/MetaHarness/ActiveConfigSnapshotBuilder.cs:23-28`
**DI Registration:** `src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs:80`

**Problem:**
The builder is registered as a Singleton (`services.AddSingleton<ISnapshotBuilder, ActiveConfigSnapshotBuilder>()`), and the constructor captures `options.CurrentValue` into a `readonly` field:

```csharp
_config = options.CurrentValue;   // Snapshot taken once at construction
```

`IOptionsMonitor<T>` exists specifically to support config reload without restarting the application. By capturing `.CurrentValue` in a Singleton constructor, any changes to `MetaHarnessConfig` in `appsettings.json` at runtime are silently ignored. The `SnapshotConfigKeys` and `SecretsRedactionPatterns` lists will remain frozen at their construction-time values for the lifetime of the process.

This is especially dangerous for `SecretsRedactionPatterns` (lines 119-120 of `MetaHarnessConfig.cs`): if an operator adds a new secret pattern at runtime, the builder will continue leaking those keys into snapshots.

**Recommended fix -- Option A (preferred):** Store the monitor, read `.CurrentValue` on each call:

```csharp
private readonly IOptionsMonitor<MetaHarnessConfig> _options;
private readonly ISecretRedactor _redactor;

public ActiveConfigSnapshotBuilder(
    IOptionsMonitor<MetaHarnessConfig> options,
    ISecretRedactor redactor)
{
    _options = options;
    _redactor = redactor;
}

// In BuildConfigSnapshot and anywhere else:
var config = _options.CurrentValue;  // Fresh read each invocation
```

**Recommended fix -- Option B:** Change the DI lifetime to Scoped or Transient so the class is re-created and re-reads config naturally. However, this only works if consumers request it within a scope.

**Blocker:** No, but the combination of Singleton + captured config is a known anti-pattern with `IOptionsMonitor<T>` and should be corrected.

---

### M-3 | MEDIUM | SHA256 hash computed over pre-redaction content but snapshot stores post-redaction content

**File:** `src/Content/Infrastructure/Infrastructure.AI/MetaHarness/ActiveConfigSnapshotBuilder.cs:43-55`

**Problem:**
The `SnapshotManifest` entries are built with a SHA256 hash of the raw (pre-redaction) file content:

```csharp
// Line 53: kvp.Value is the RAW file content (before redaction)
ComputeSha256Hex(Encoding.UTF8.GetBytes(kvp.Value))
```

But `SkillFileSnapshots` stores the redacted content:

```csharp
// Lines 45-48: values are redacted
kvp => _redactor.Redact(kvp.Value) ?? string.Empty
```

This creates a verification mismatch: if a downstream consumer tries to verify snapshot integrity by hashing the stored `SkillFileSnapshots` values and comparing against `SnapshotManifest` hashes, the hashes will never match for any file that contained redacted content. The manifest becomes useless as an integrity check.

Additionally, this means the pre-redaction content hash is persisted to disk. While a SHA256 hash is not reversible, it does allow an attacker to confirm whether a guessed secret value was present in a specific file (hash the guess, compare to manifest).

**Recommended fix:**
Compute redacted values once, then use them for both the snapshot dictionary and the manifest hashes:

```csharp
var redactedFiles = skillFiles.ToDictionary(
    kvp => kvp.Key,
    kvp => _redactor.Redact(kvp.Value) ?? string.Empty);

return new HarnessSnapshot
{
    SkillFileSnapshots = redactedFiles,
    SystemPromptSnapshot = redactedPrompt,
    ConfigSnapshot = configSnapshot,
    SnapshotManifest = redactedFiles
        .Select(kvp => new SnapshotEntry(
            kvp.Key,
            ComputeSha256Hex(Encoding.UTF8.GetBytes(kvp.Value))))
        .ToList()
};
```

This ensures the manifest hashes match the stored content and avoids calling `Redact()` twice per file.

**Blocker:** No, but the manifest is currently incorrect and misleading. Fix before any downstream code relies on it for integrity verification.

---

## Fix Priority

| ID   | Severity | Effort | Recommendation |
|------|----------|--------|----------------|
| H-1  | HIGH     | Low    | Fix before merge -- delete duplicate, delegate to ISecretRedactor |
| M-1  | MEDIUM   | Low    | Fix before merge -- add path canonicalization + containment check |
| M-2  | MEDIUM   | Low    | Fix before merge -- store IOptionsMonitor, read .CurrentValue per call |
| M-3  | MEDIUM   | Low    | Fix before merge -- hash post-redaction content, compute redacted values once |

All four fixes are low-effort (under 10 lines each) and address correctness or security concerns. None require architectural changes.
