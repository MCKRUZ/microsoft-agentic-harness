# Code Review: Section 02 -- Domain Resilience Models

**Reviewer:** claude-code-reviewer  
**Date:** 2026-05-08  
**Scope:** 3 new source files (1 enum, 1 sealed record, 1 sealed exception class) + 1 test file (6 tests)  
**Verdict:** **Approve with warnings** -- no CRITICAL or HIGH issues. Two MEDIUM items and several LOW items to consider.

---

## MEDIUM -- ProviderExhaustedException stores mutable list reference without defensive copy

**File:** `ProviderExhaustedException.cs:16-20`

```csharp
public ProviderExhaustedException(IReadOnlyList<string> failedProviders, TimeSpan retryAfter)
    : base($"All LLM providers exhausted: {string.Join(", ", failedProviders)}. Retry after {retryAfter.TotalSeconds}s.")
{
    FailedProviders = failedProviders;
    RetryAfter = retryAfter;
}
```

`IReadOnlyList<string>` is an interface -- the caller can pass a `List<string>` and mutate it after the exception is constructed. The message is built from the snapshot at construction time, but `FailedProviders` would reflect the mutated list, creating a mismatch between `Message` and `FailedProviders`.

This matters because:
1. Exception objects frequently cross thread boundaries (catch in one thread, log/inspect in another)
2. The exception is `sealed` so no subclass can fix it
3. `FallbackMetadata` (a record) gets automatic immutability guarantees from `required init`, but the exception constructor does not

**Fix:**

```csharp
FailedProviders = failedProviders.ToArray(); // defensive copy
```

`Array` implements `IReadOnlyList<string>`, so the property type stays the same. Same fix needed in both constructors.

**Impact:** Without this fix, a caller doing `var list = new List<string>{"a"}; var ex = new ProviderExhaustedException(list, ...); list.Add("b");` silently corrupts the exception state.

---

## MEDIUM -- Test file uses raw xUnit Assert.* instead of project-standard FluentAssertions

**File:** `ResilienceDomainModelTests.cs` (all 6 tests)

The `Domain.AI.Tests` project references `FluentAssertions` and ~50 existing test files use it exclusively. This test file uses raw `Assert.True`, `Assert.Contains`, `Assert.Equal`, `Assert.Same`, etc.

**Example (current):**
```csharp
Assert.True(metadata.IsFallback);
Assert.Contains("primary", metadata.FailedProviders);
```

**Example (project convention):**
```csharp
metadata.IsFallback.Should().BeTrue();
metadata.FailedProviders.Should().Contain("primary");
```

**Why it matters:** Mixed assertion styles in the same test project force contributors to check which style a given file uses. FluentAssertions also produces better failure messages (e.g., `Expected collection to contain "primary" but found {"secondary"}` vs. xUnit generic `Assert.Contains failed`).

**Fix:** Convert all 6 tests to FluentAssertions. Add `using FluentAssertions;` at the top.

---

## LOW -- ProviderExhaustedException missing parameterless and message-only constructors

**File:** `ProviderExhaustedException.cs`

.NET exception design guidelines (CA1032) recommend four constructors:
1. `()` -- parameterless
2. `(string message)` -- message only
3. `(string message, Exception inner)` -- message + inner
4. `(SerializationInfo, StreamingContext)` -- serialization (obsolete in .NET 8+, so rightfully omitted)

The exception has two custom constructors but skips (1) and (2). Since the exception is `sealed`, this only matters for framework interop (e.g., `Activator.CreateInstance`, certain serializers, generic exception-handling middleware that catches and re-throws with modified messages).

The existing `InvalidStateTransitionException` and `DecisionEvaluationException` in `Domain.Common` also skip the parameterless constructor, so this is consistent with the codebase.

**Verdict:** No action required. Noted for awareness. The `sealed` modifier makes this a non-issue in practice -- no one will subclass it.

---

## LOW -- FallbackMetadata.IsFallback is derivable from FailedProviders

**File:** `FallbackMetadata.cs:19`

```csharp
public required bool IsFallback { get; init; }
```

`IsFallback` is semantically `FailedProviders.Count > 0`. Having both as independent `required` properties creates a representability problem: you can construct `IsFallback = false` with `FailedProviders = ["primary"]`, which is a logical contradiction.

**Options:**
1. **Recommended:** Make `IsFallback` a computed property: `public bool IsFallback => FailedProviders.Count > 0;` -- eliminates the invalid state, reduces the constructor surface, and is still a readable property.
2. **Alternative:** Keep as-is if there are edge cases where the active provider changed but no providers "failed" (e.g., proactive rebalancing). If so, add an XML doc comment explaining when `IsFallback = true` with empty `FailedProviders` is valid.

**Impact:** Low. The record is constructed in one place (`ResilientChatClient`, per the XML docs) so the risk of inconsistent construction is small, but the computed property eliminates it entirely.

---

## LOW -- No negative/edge-case tests for ProviderExhaustedException

**File:** `ResilienceDomainModelTests.cs`

Missing test cases:
1. **Empty `FailedProviders` list** -- what does the message look like? Is this a valid state? (An exception saying "All LLM providers exhausted: " with no provider names is confusing.)
2. **`TimeSpan.Zero` or negative `RetryAfter`** -- valid? The message would say "Retry after 0s" or "Retry after -30s". Should the constructor guard against non-positive values?
3. **`null` `FailedProviders`** -- the `string.Join` in the base message would throw `ArgumentNullException` during construction, but there is no explicit guard with a clear error message.

**Suggested tests:**
```csharp
[Fact]
public void ProviderExhaustedException_NullProviders_ThrowsArgumentNullException()
{
    // Documents that null is invalid at construction time
    var act = () => new ProviderExhaustedException(null!, TimeSpan.FromSeconds(10));
    act.Should().Throw<ArgumentNullException>();
}

[Fact]
public void ProviderExhaustedException_EmptyProviders_FormatsMessageCleanly()
{
    var ex = new ProviderExhaustedException([], TimeSpan.FromSeconds(5));
    ex.Message.Should().Contain("Retry after 5s");
    ex.FailedProviders.Should().BeEmpty();
}
```

If `null` or empty providers should be invalid, add a guard clause in the constructor.

---

## LOW -- No record equality or with expression tests for FallbackMetadata

**File:** `ResilienceDomainModelTests.cs`

Same gap as the section-01 review noted for Escalation records. C# records use reference equality for collection members, meaning two `FallbackMetadata` instances with identical `FailedProviders` content but different list references will NOT be equal. This is a common gotcha worth documenting via test.

**Suggested test:**
```csharp
[Fact]
public void FallbackMetadata_SameContentDifferentListReference_AreNotEqual()
{
    var a = new FallbackMetadata
    {
        ActiveProvider = "p", IsFallback = false,
        FailedProviders = new List<string>(),
        DisabledCapabilities = new HashSet<string>(),
        CircuitStates = new Dictionary<string, ProviderHealthState>()
    };
    var b = a with { FailedProviders = new List<string>() };
    // Documents the C# record collection equality gotcha
    a.Should().NotBe(b);
}
```

---

## INFO -- Clean Architecture compliance verified

- **No framework dependencies in Domain:** The three new files use only `System` and `System.Collections.Generic` types (`IReadOnlyList<T>`, `IReadOnlySet<T>`, `IReadOnlyDictionary<K,V>`, `TimeSpan`, `Exception`). No external packages, no infrastructure concerns. `IReadOnlySet<T>` is in BCL since .NET 7, confirmed used elsewhere in this codebase (`ToolPermissionFilter.cs`, `RestrictedSearchTool.cs`).
- **No Infrastructure leakage:** No `System.Text.Json`, `System.Net.Http`, Polly, or external SDK references. The `ProviderHealthState` enum mentions Polly circuit breaker states in XML docs only -- no compile-time dependency.
- **Immutability:** `FallbackMetadata` uses `required init` with `IReadOnlyList`, `IReadOnlySet`, `IReadOnlyDictionary` for all collection properties. `ProviderExhaustedException` uses get-only properties. The mutable-reference concern on the exception constructor is flagged separately as MEDIUM.
- **File sizes:** All files well under 150 lines (largest is the test file at 100 lines). Well within project guidelines.
- **XML documentation:** Complete on all public types, enum members, constructors, and properties. Cross-references to `ResilientChatClient` and `ProviderCapabilityRegistry` via `<c>` tags provide context for where these types will be constructed and consumed.
- **Naming:** Consistent PascalCase. Follows the `Provider*` prefix convention for the resilience subsystem. Enum values are clear and self-documenting.
- **No secrets, no hardcoded values, no injection risks:** Pure domain types with no I/O.

---

## INFO -- Test coverage assessment

6 tests covering 3 source types:

| Type | Tests | Coverage Notes |
|------|-------|----------------|
| `FallbackMetadata` | 3 | No-fallback, with-fallback, disabled capabilities |
| `ProviderExhaustedException` | 2 | Basic construction, inner exception wrapping |
| `ProviderHealthState` | 1 | Numeric ordering comparison |

**Gaps:**
- No edge case tests for exception (null providers, empty providers, zero/negative retry -- see LOW finding above)
- No record equality or `with` tests for `FallbackMetadata` (see LOW finding above)
- No test verifying `RetryAfter` is included in the exception `Message` string (currently only tests that provider names appear in message)
- No test for `FallbackMetadata` with all providers failed (multiple entries in `FailedProviders`)
- Enum test is minimal (just ordering) -- could add `Enum.GetValues` count test to catch accidental additions

---

## INFO -- Design observation: IsFallback redundancy is a deliberate trade-off

The `IsFallback` property on `FallbackMetadata` is flagged as LOW because it is derivable from `FailedProviders.Count > 0`. However, there is a reasonable argument for keeping it: it serves as an explicit signal for consumers who want a quick boolean check without reasoning about list semantics. The `ResilientChatClient` (documented as the sole constructor) can enforce consistency. This is a readability-vs-invariant trade-off, and the current choice prioritizes readability. Both options are defensible.

---

## Summary

| Severity | Count | Action |
|----------|-------|--------|
| CRITICAL | 0 | -- |
| HIGH | 0 | -- |
| MEDIUM | 2 | Should fix before merge |
| LOW | 4 | Consider improving |
| INFO | 3 | Awareness only |

**Verdict: Approve with warnings.** The domain types are clean, minimal, and correctly layered. The enum is well-designed with explicit numeric values enabling comparison operators. The record uses appropriate read-only collection interfaces throughout. The exception carries useful structured data for resilience scenarios.

**Recommended fix order:**
1. Defensive copy in `ProviderExhaustedException` constructor (thread safety, data integrity)
2. Convert tests to FluentAssertions (project consistency)
3. Consider making `IsFallback` a computed property (eliminate invalid state)
4. Add edge-case tests for exception construction (null/empty/zero guards)
