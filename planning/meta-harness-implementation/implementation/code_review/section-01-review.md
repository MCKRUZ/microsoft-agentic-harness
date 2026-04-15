# Code Review: section-01-config (MetaHarnessConfig)

## CRITICAL

**1. Tests use direct instantiation, not IOptions<T> binding**
`MetaHarnessConfigTests.cs` — every test body does `new MetaHarnessConfig()`. The section spec requires `ServiceCollection` + `IOptions<T>`. Direct construction only proves the C# field initializer; a broken `Bind()` call would still pass all 6 tests. The binding path is not exercised.

**2. IReadOnlyList<string> properties will silently fail to bind from appsettings.json overrides**
`MetaHarnessConfig.cs` lines ~141, ~149. The configuration binder resolves list properties by calling `Add()`, which `IReadOnlyList<string>` does not support. Overriding `SnapshotConfigKeys` or `SecretsRedactionPatterns` from appsettings.json will silently use code defaults. (Note: this matches the existing `FileSystemConfig.AllowedBasePaths` pattern — may be an accepted codebase limitation.)

## HIGH

**3. Doc comment mismatch on `DefaultBinding_PopulatesAllDefaults`**
Summary says "Binds all properties from IOptions with an empty config source" but body does no IOptions binding. Template project — misleading teaching material.

**4. SecretsRedactionPatterns assertion in master test is weaker than dedicated test**
Line ~233: `Should().HaveCount(5)` — tests count only. Should use `ContainInOrder` with explicit values, consistent with the dedicated test.

## MEDIUM

**5. Redundant inline comment duplicates remarks XML block**
`MetaHarnessConfig.cs` line ~58: `// Mutable setters required by IOptionsMonitor<T> binding.` is word-for-word from the `<remarks>` block above it.

**6. Relative path defaults lack resolution contract in docs**
`TraceDirectoryRoot` ("traces") and `EvalTasksPath` ("eval-tasks") don't document whether relative paths resolve against working directory, assembly location, or a base path config key.

**7. MaxEvalParallelism: no ceiling or 0-value behavior documented**
Value of 0 would be silently accepted and could deadlock at runtime.

## LOW

**8. Plan text says "15 properties" but table + implementation has 16**
Section plan body (line 21) says "All 15 config properties" but table has 16 rows. Implementation is correct. Plan text needs a correction.

**9. JSON section defaults create dual-maintenance hazard**
7 JSON keys set values identical to code defaults. Deliberate plan decision (per spec line 97), but worth a policy comment.

## POSITIVE

- All 16 properties with correct types, defaults, and full XML `<value>` tags.
- No secrets, connection strings, or hardcoded external references anywhere.
- `EnableShellTool` defaults false with explicit security rationale in doc.
- `AppConfig.cs` hierarchy comment updated correctly.
- `SecretsRedactionPatterns` includes "ConnectionString" — the most common omission.
