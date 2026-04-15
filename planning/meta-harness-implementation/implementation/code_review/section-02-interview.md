# Code Review Interview: section-02-secret-redaction

## No User Input Required — All Auto-Fixed

---

## Auto-Fixes Applied

### CRITICAL #1 — Idempotency violated (patterns 2 & 3 re-match `[REDACTED]`)
**Fix:** Added `(?!\[REDACTED\])` negative lookahead on the value-side of patterns 2 and 3:
- Pattern 2: `[^;"'\s]+` → `(?!\[REDACTED\])[^;"'\s]+`
- Pattern 3: `\S+` → `(?!\[REDACTED\])\S+`
Pattern 1 (Bearer) was already idempotent — the character class `[A-Za-z0-9\-._~+/]` excludes `[` and `]`.

### CRITICAL #2 — Missing idempotency test
**Fix:** Added `Redact_AlreadyRedactedString_ReturnsUnchanged` — calls `Redact("AccountKey=[REDACTED]")` and asserts the output equals the input.

### HIGH #3 — Null dereference if `SecretsRedactionPatterns` absent from config
**Fix:** Added `?? []` null-coalescing guard in the public constructor:
`config.CurrentValue.SecretsRedactionPatterns ?? []`

### HIGH #4 — Bearer token pattern case-sensitive
**Fix:** Added `RegexOptions.IgnoreCase` to the Bearer pattern alongside `RegexOptions.Compiled`.

### MEDIUM #5 — `IsSecretKey(null)` throws
**Fix:** Added `if (string.IsNullOrEmpty(configKey)) return false;` guard at top of `IsSecretKey`.

### MEDIUM #6 — Internal constructor never directly tested
**Fix:** Changed constructor visibility from `internal` to `public` (template project — test accessibility is a valid consumer concern). Added `DirectListConstructor_WithExplicitDenylist_UsesProvidedPatterns` test.

### LOW #7 — `_redactionPatterns` mutable array type
**Fix:** Changed type to `IReadOnlyList<(Regex, string)>` and initialized via `Array.AsReadOnly(BuildRedactionPatterns())`.

## Items Let Go

### LOW #8 — No `RegexOptions.NonBacktracking`
`NonBacktracking` is incompatible with negative lookaheads (`(?!\[REDACTED\])`), which are required for idempotency in patterns 2 and 3. `Compiled` is kept for performance. The ReDoS surface is bounded — the patterns have no catastrophic backtracking characteristics.
