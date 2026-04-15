# Code Review: section-02-secret-redaction

## CRITICAL

**1. Idempotency violated — patterns 2 & 3 re-match `[REDACTED]`**
`PatternSecretRedactor.cs` lines ~172-183. Pattern 2's `[^;"'\s]+` and pattern 3's `\S+` both match the string `[REDACTED]` since it contains no semicolons/quotes/whitespace. Running `Redact` twice on already-redacted output produces additional allocations and breaks the idempotency contract.
Fix: add `(?!\[REDACTED\])` negative lookahead on value-side of patterns 2 and 3.

**2. Missing idempotency test**
Only 7 of the spec's 8 tests delivered. The absent one is `Redact_AlreadyRedactedString_ReturnsUnchanged`. This is the only test that catches issue #1.

## HIGH

**3. Null dereference if `SecretsRedactionPatterns` is absent from config**
`PatternSecretRedactor.cs` line ~44. `config.CurrentValue.SecretsRedactionPatterns` can be null if the MetaHarness config section is absent. Delegation to `this(null)` causes a NullReferenceException in `IsSecretKey` at first call.
Fix: `config.CurrentValue.SecretsRedactionPatterns ?? []`

**4. Bearer token pattern is case-sensitive**
`PatternSecretRedactor.cs` line ~164. HTTP Authorization headers are case-insensitive. `bearer <token>` (lowercase) in trace logs would go unredacted. Patterns 2 & 3 use `(?i)` but pattern 1 does not.
Fix: add `RegexOptions.IgnoreCase` to Bearer pattern.

## MEDIUM

**5. `IsSecretKey(null)` throws with no documented contract**
`string.Contains` throws `ArgumentNullException` when `configKey` is null. Add a null guard returning `false` (unknown key = not secret).

**6. Internal constructor never directly tested**
Documented as "Intended for testing" but all 7 tests use the public constructor via Moq. Add one direct test.

## LOW

**7. `_redactionPatterns` is a mutable array**
Inconsistent with project immutability style. Change to `IReadOnlyList<(Regex, string)>` + `Array.AsReadOnly(...)`.

**8. No `RegexOptions.NonBacktracking` on security patterns**
This component processes untrusted LLM output. NonBacktracking guarantees linear-time execution, eliminating the ReDoS surface.

## POSITIVE

- Interface XML docs are thorough; dual-boundary model, thread-safety, and idempotency contracts clearly documented.
- Constructor delegation pattern is clean and well-documented.
- Immutable denylist snapshot with documented rationale for no runtime reload.
- `Redact(null/empty)` guard is correct and tested.
- `AddSingleton` registration is correct for this stateless-after-construction type.
- `RegexOptions.Compiled` on all patterns appropriate for a singleton on a hot path.
