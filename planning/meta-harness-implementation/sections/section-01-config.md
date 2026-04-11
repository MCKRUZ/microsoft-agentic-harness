# Section 01: Config (`MetaHarnessConfig`)

## Overview

This section introduces `MetaHarnessConfig` — the strongly-typed configuration POCO that gates everything else in the meta-harness implementation. Every subsequent section reads from this config. It must exist before any other section can be implemented.

**This section has no dependencies on other sections.**

It is parallelizable with `section-02-secret-redaction`.

---

## What to Build

### 1. Config POCO

**File:** `src/Content/Domain/Domain.Common/Config/MetaHarness/MetaHarnessConfig.cs`

A new config class following the exact same pattern as `InfrastructureConfig` — a plain C# class with `{ get; set; }` properties and inline defaults, XML-documented on all public members, placed in the `Domain.Common.Config.MetaHarness` namespace.

All 16 properties with their defaults (note: plan body text incorrectly said 15; table and implementation both have 16):

| Property | Type | Default | Purpose |
|---|---|---|---|
| `TraceDirectoryRoot` | `string` | `"traces"` | Root path for all trace output |
| `MaxIterations` | `int` | `10` | Iterations per optimization run |
| `SearchSetSize` | `int` | `50` | Max eval tasks per candidate |
| `ScoreImprovementThreshold` | `double` | `0.01` | Min pass-rate delta to count as improvement |
| `AutoPromoteOnImprovement` | `bool` | `false` | Auto-apply best candidate; false = write to disk only |
| `EvalTasksPath` | `string` | `"eval-tasks"` | Path to eval task JSON files |
| `SeedCandidatePath` | `string` | `""` | Optional path to seed harness snapshot |
| `MaxEvalParallelism` | `int` | `1` | Controlled parallelism for eval tasks (1 = sequential) |
| `EvaluationTemperature` | `double` | `0.0` | LLM temperature for deterministic eval |
| `EvaluationModelVersion` | `string?` | `null` | Optional model override for eval (null = use default) |
| `SnapshotConfigKeys` | `IReadOnlyList<string>` | `[]` | AppConfig keys to include in harness snapshot |
| `SecretsRedactionPatterns` | `IReadOnlyList<string>` | `["Key","Secret","Token","Password","ConnectionString"]` | Config key substrings never snapshotted |
| `MaxFullPayloadKB` | `int` | `512` | Max size for per-call full payload artifacts |
| `MaxRunsToKeep` | `int` | `20` | How many optimization runs to retain (0 = unlimited) |
| `EnableShellTool` | `bool` | `false` | Opt-in: allow proposer to run restricted shell commands |
| `EnableMcpTraceResources` | `bool` | `true` | Expose traces via MCP resources |

The class must use `IReadOnlyList<string>` (not `List<string>`) for collection properties, consistent with the `FileSystemConfig.AllowedBasePaths` pattern in this codebase. Default collection values use array initializer syntax (`[]`).

Stub shape (fill in XML docs and defaults per the table above):

```csharp
namespace Domain.Common.Config.MetaHarness;

/// <summary>
/// Configuration for the meta-harness optimization loop.
/// Binds to <c>AppConfig.MetaHarness</c> in appsettings.json.
/// </summary>
public class MetaHarnessConfig
{
    public string TraceDirectoryRoot { get; set; } = "traces";
    public int MaxIterations { get; set; } = 10;
    // ... all 15 properties
}
```

### 2. Wire into `AppConfig`

**File:** `src/Content/Domain/Domain.Common/Config/AppConfig.cs`

Add a `MetaHarness` property to the `AppConfig` class and update the XML doc comment hierarchy block.

```csharp
using Domain.Common.Config.MetaHarness;

// Add to AppConfig class body:
/// <summary>
/// Gets or sets the meta-harness optimization loop configuration.
/// </summary>
public MetaHarnessConfig MetaHarness { get; set; } = new();
```

Also update the `<code>` block in the class-level XML doc to include `└── MetaHarness — Automated harness optimization loop`.

### 3. Add `appsettings.json` section

**File:** `src/Content/Presentation/Presentation.ConsoleUI/appsettings.json`

Add the `"MetaHarness"` section inside `"AppConfig"` with the explicitly configured defaults:

```json
"MetaHarness": {
  "TraceDirectoryRoot": "traces",
  "MaxIterations": 10,
  "EvalTasksPath": "eval-tasks",
  "MaxEvalParallelism": 1,
  "MaxRunsToKeep": 20,
  "EnableShellTool": false,
  "EnableMcpTraceResources": true
}
```

Properties intentionally omitted from the JSON section (rely on code defaults): `SearchSetSize`, `ScoreImprovementThreshold`, `AutoPromoteOnImprovement`, `SeedCandidatePath`, `EvaluationTemperature`, `EvaluationModelVersion`, `SnapshotConfigKeys`, `SecretsRedactionPatterns`, `MaxFullPayloadKB`.

---

## Tests

**Test project:** `src/Content/Tests/Application.AI.Common.Tests/` (or `Domain.Common.Tests` if it exists — check before creating)

**File:** `src/Content/Tests/Application.AI.Common.Tests/Config/MetaHarnessConfigTests.cs`

Six tests covering binding and defaults. All tests use `Microsoft.Extensions.Configuration` in-memory binding — no mocks needed, no external services.

Test stubs:

```csharp
namespace Application.AI.Common.Tests.Config;

public class MetaHarnessConfigTests
{
    /// <summary>Binds all properties from IOptions with an empty config source — verifies defaults.</summary>
    [Fact]
    public void MetaHarnessConfig_DefaultBinding_PopulatesAllDefaults() { }

    /// <summary>TraceDirectoryRoot defaults to "traces" when not present in config.</summary>
    [Fact]
    public void TraceDirectoryRoot_NotConfigured_DefaultsToTraces() { }

    /// <summary>MaxIterations defaults to 10.</summary>
    [Fact]
    public void MaxIterations_NotConfigured_DefaultsToTen() { }

    /// <summary>SecretsRedactionPatterns contains Key, Secret, Token, Password, ConnectionString.</summary>
    [Fact]
    public void SecretsRedactionPatterns_NotConfigured_ContainsExpectedDefaults() { }

    /// <summary>EnableShellTool defaults to false.</summary>
    [Fact]
    public void EnableShellTool_NotConfigured_DefaultsToFalse() { }

    /// <summary>MaxEvalParallelism defaults to 1.</summary>
    [Fact]
    public void MaxEvalParallelism_NotConfigured_DefaultsToOne() { }
}
```

Each test uses a private `ResolveDefaults()` helper that builds a `ServiceCollection`, calls `services.Configure<MetaHarnessConfig>(_ => { })`, builds the provider, and resolves `IOptions<MetaHarnessConfig>.Value`. This exercises the DI registration path without requiring `ConfigurationBuilder` (not available in the test project's package set). The binding test (`DefaultBinding_PopulatesAllDefaults`) asserts all 16 properties match defaults using `ContainInOrder` for collection assertions.

> **Implementation note:** `IReadOnlyList<string>` properties (`SnapshotConfigKeys`, `SecretsRedactionPatterns`) cannot be overridden via `appsettings.json` (binder requires a mutable `Add()` surface). This is consistent with the `FileSystemConfig.AllowedBasePaths` pattern; configure these in code only. Documented in `<remarks>` blocks on each property.

---

## Acceptance Criteria

1. `dotnet build src/AgenticHarness.slnx` passes with zero warnings in `Domain.Common`.
2. All 6 tests pass: `dotnet test src/AgenticHarness.slnx --filter "FullyQualifiedName~MetaHarnessConfigTests"`.
3. `AppConfig.MetaHarness` is resolvable via `IOptionsMonitor<AppConfig>` — no new DI registration needed (binds with the existing `services.Configure<AppConfig>(...)` call).
4. No secrets or connection strings in the new config class or JSON section.

---

## Files to Create/Modify

| Action | Path |
|---|---|
| Create | `src/Content/Domain/Domain.Common/Config/MetaHarness/MetaHarnessConfig.cs` |
| Modify | `src/Content/Domain/Domain.Common/Config/AppConfig.cs` |
| Modify | `src/Content/Presentation/Presentation.ConsoleUI/appsettings.json` |
| Create | `src/Content/Tests/Application.AI.Common.Tests/Config/MetaHarnessConfigTests.cs` |

---

## Patterns to Follow

- All `{ get; set; }` properties (not `init`) — required by `IOptionsMonitor<T>` binding, as noted in the `AppConfig` class comment.
- `IReadOnlyList<string>` for collection surfaces — matches `FileSystemConfig.AllowedBasePaths` exactly.
- Full XML documentation on all public members — this is a template project; docs are teaching material.
- No framework dependencies in `Domain.Common` — this class must compile with zero package references beyond `netX.X` BCL.
- Follow the `InfrastructureConfig` file as the style reference: namespace declaration, class structure, property ordering.
