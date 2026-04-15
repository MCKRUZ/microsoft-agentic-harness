# Code Review Interview: section-01-config

## Items Raised to User

### CRITICAL #2 — IReadOnlyList<string> binding limitation
**Decision:** Accept as-is (consistent with `FileSystemConfig.AllowedBasePaths` pattern)
**Action:** Documented binding limitation in `<remarks>` blocks on `SnapshotConfigKeys` and `SecretsRedactionPatterns`.

---

## Auto-Fixes Applied

### CRITICAL #1 — Tests used direct instantiation instead of IOptions<T>
**Fix:** Replaced `new MetaHarnessConfig()` with `ServiceCollection.Configure<MetaHarnessConfig>(_ => {})` + `IOptions<T>` resolution via `BuildServiceProvider()`. Exercises the DI registration path without requiring `ConfigurationBuilder`.

### HIGH #3 — Doc comment mismatch on DefaultBinding test
**Fix:** Updated summary from "Binds all properties from IOptions with an empty config source" to "Resolves MetaHarnessConfig via IOptions with no overrides — verifies all property defaults."

### HIGH #4 — SecretsRedactionPatterns assertion used HaveCount(5) in master test
**Fix:** Changed to `ContainInOrder("Key", "Secret", "Token", "Password", "ConnectionString")` — consistent with the dedicated test.

### MEDIUM #5 — Redundant inline comment
**Fix:** Removed `// Mutable setters required by IOptionsMonitor<T> binding.` from below the `</remarks>` tag — already stated in the `<remarks>` block.

### MEDIUM #6 — Path resolution contract missing
**Fix:** Added "Relative paths are resolved against the working directory at runtime." to `TraceDirectoryRoot` and `EvalTasksPath` XML docs.

---

## Items Let Go

- LOW #7 (MaxEvalParallelism ceiling/0-value): Validation belongs in a FluentValidation command validator, not the config class.
- LOW #8 (Plan text says "15" but table has 16): Implementation is correct (16); fixing the plan doc in section update step.
- LOW #9 (JSON dual-maintenance hazard): Deliberate plan decision per spec line 97.
