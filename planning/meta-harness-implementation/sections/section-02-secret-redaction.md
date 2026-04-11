# Section 2: Secret Redaction (`ISecretRedactor`)

## Overview

This section implements the secret redaction system used throughout the meta-harness to ensure secrets never reach disk. It is a foundational component with no dependencies on other sections — it can be implemented and tested independently.

The system has two layers of protection:
1. **Config key filtering** — when building `HarnessSnapshot`, any config key whose name contains a pattern from `SecretsRedactionPatterns` is excluded from the snapshot entirely.
2. **Free-text redaction** — when writing traces, tool results, system prompts, or any string content to disk, known secret-like patterns (Bearer tokens, connection strings, etc.) are replaced with `"[REDACTED]"`.

## Dependency Context

This section has **no dependencies** on other sections. It blocks:
- `section-04-trace-infrastructure` (trace store applies redactor before all writes)
- `section-09-candidate-domain` (snapshot builder applies redactor to system prompt and excludes secret config keys)

The `SecretsRedactionPatterns` config property (defined in `section-01-config`) is the source of the denylist. If `section-01-config` is not yet complete, use a hardcoded default list for testing: `["Key", "Secret", "Token", "Password", "ConnectionString"]`.

## Files to Create

### Interface

**`src/Content/Application/Application.AI.Common/Interfaces/ISecretRedactor.cs`**

```csharp
/// <summary>
/// Redacts secrets from free-text strings and filters secret config keys.
/// Applied before any content is persisted to disk (traces, snapshots, manifests).
/// </summary>
public interface ISecretRedactor
{
    /// <summary>
    /// Scans <paramref name="input"/> for known secret patterns and replaces
    /// matches with "[REDACTED]". Returns the original string if no matches found.
    /// Returns null/empty unchanged.
    /// </summary>
    string? Redact(string? input);

    /// <summary>
    /// Returns true if <paramref name="configKey"/> matches any entry in the
    /// secrets denylist and should therefore be excluded from config snapshots.
    /// </summary>
    bool IsSecretKey(string configKey);
}
```

### Implementation

**`src/Content/Infrastructure/Infrastructure.AI/Security/PatternSecretRedactor.cs`**

`PatternSecretRedactor` is a singleton that takes `IOptionsMonitor<MetaHarnessConfig>` (or just the pattern list) in its constructor. It pre-compiles two sets of regex patterns at construction time:

1. **Denylist key patterns** — from `MetaHarnessConfig.SecretsRedactionPatterns`. Used by `IsSecretKey`: a case-insensitive substring check against the config key name (no regex needed — `string.Contains` with `OrdinalIgnoreCase` is sufficient and safer).

2. **Free-text redaction patterns** — a hardcoded set of compiled `Regex` objects targeting common secret shapes:
   - Bearer tokens: `Bearer\s+[A-Za-z0-9\-._~+/]+=*` → replace entire match with `Bearer [REDACTED]`
   - Connection strings: `(?i)(AccountKey|Password|pwd|SharedAccessKey)\s*=\s*[^;"\s]+` → replace value portion with `[REDACTED]`
   - Generic key=value secrets: `(?i)(api[_-]?key|access[_-]?token|secret[_-]?key)\s*[=:]\s*\S+` → replace value with `[REDACTED]`

The `Redact(string? input)` method iterates the compiled patterns and applies each in sequence. If no pattern matches, the original string is returned unchanged (no allocation).

Constructor signatures:
```csharp
public PatternSecretRedactor(IOptionsMonitor<MetaHarnessConfig> config)      // DI path — null-coalesces SecretsRedactionPatterns ?? []
public PatternSecretRedactor(IReadOnlyList<string> denylistPatterns)          // Direct path — for testing without DI
```

> **Implementation notes:**
> - Patterns 2 and 3 include `(?!\[REDACTED\])` negative lookaheads for idempotency — `[REDACTED]` cannot re-match on a second `Redact()` call.
> - Bearer pattern uses `RegexOptions.IgnoreCase` (HTTP headers are case-insensitive).
> - `_redactionPatterns` is `IReadOnlyList<(Regex, string)>` via `Array.AsReadOnly()`.
> - `IsSecretKey(null/empty)` returns `false` safely.
> - `RegexOptions.NonBacktracking` is incompatible with the negative lookaheads required for idempotency; `Compiled` is used instead.

## DI Registration

**Modified file:** `src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs`

Register as singleton:
```csharp
services.AddSingleton<ISecretRedactor, PatternSecretRedactor>();
```

## Tests

**Test project:** `src/Content/Tests/Infrastructure.AI.Tests/`

**New file:** `src/Content/Tests/Infrastructure.AI.Tests/Security/PatternSecretRedactorTests.cs`

The test class should arrange a `PatternSecretRedactor` instance with a known denylist (`["Key", "Secret", "Token", "Password", "ConnectionString"]`). Use constructor injection with a stub `IOptionsMonitor<MetaHarnessConfig>` or the direct list overload.

### Test stubs

```csharp
public class PatternSecretRedactorTests
{
    /// <summary>
    /// A string containing "Authorization: Bearer eyABC123..." has the token value
    /// replaced with "[REDACTED]", leaving the "Bearer" prefix intact.
    /// </summary>
    [Fact]
    public void Redact_StringContainingBearerToken_ReplacesWithRedacted() { }

    /// <summary>
    /// A plain string with no secret patterns is returned exactly as-is
    /// (same reference or equal value, no mutation).
    /// </summary>
    [Fact]
    public void Redact_StringWithNoSecrets_ReturnsUnchanged() { }

    /// <summary>
    /// A config key named "AzureOpenAIApiKey" matches the "Key" pattern and
    /// IsSecretKey returns true.
    /// </summary>
    [Fact]
    public void IsSecretKey_KeyMatchingDenylistPattern_ReturnsTrue() { }

    /// <summary>
    /// A config key named "MaxIterations" does not match any denylist pattern
    /// and IsSecretKey returns false.
    /// </summary>
    [Fact]
    public void IsSecretKey_KeyNotMatchingAnyPattern_ReturnsFalse() { }

    /// <summary>
    /// IsSecretKey matching is case-insensitive: "apikey" matches "Key".
    /// </summary>
    [Fact]
    public void IsSecretKey_CaseInsensitiveMatch_ReturnsTrue() { }

    /// <summary>
    /// Redact(null) returns null without throwing.
    /// Redact("") returns "" without throwing.
    /// </summary>
    [Fact]
    public void Redact_NullOrEmpty_ReturnsInputUnchanged() { }

    /// <summary>
    /// A connection string containing "AccountKey=abc123;" has the value portion
    /// replaced with "[REDACTED]".
    /// </summary>
    [Fact]
    public void Redact_ConnectionStringWithAccountKey_RedactsValue() { }

    /// <summary>
    /// A string with multiple secret occurrences has all of them redacted,
    /// not just the first match.
    /// </summary>
    [Fact]
    public void Redact_MultipleSecretsInInput_RedactsAll() { }
}
```

## Design Notes

- **No mutation of the denylist at runtime.** The patterns are fixed at construction. If `IOptionsMonitor` is used, reloading config does NOT recompile patterns — this is intentional to avoid race conditions. The service must be restarted to pick up new patterns.
- **`IsSecretKey` is a substring check, not regex.** The denylist entries (`"Key"`, `"Secret"`, etc.) are treated as case-insensitive substrings of the config key name. This is intentional — adding regex complexity here buys nothing and introduces injection risk if patterns come from config.
- **`Redact` is idempotent.** Running it twice on the same string produces the same output. The `"[REDACTED]"` placeholder does not itself match any of the free-text patterns.
- **Thread safety.** Compiled `Regex` instances are stateless after construction. `IsSecretKey` reads an immutable list. Both methods are safe for concurrent calls.
- **Do not redact log output** — the redactor is only applied at persistence boundaries (writing to disk). It is not wired into the logging pipeline. This keeps log verbosity useful in dev while protecting disk artifacts.
