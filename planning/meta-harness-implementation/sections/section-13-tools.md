# Section 13: Tools (`RestrictedSearchTool` + `TraceResourceProvider`)

## Overview

This section implements two opt-in tools that extend the proposer's ability to read execution traces:

1. **`RestrictedSearchTool`** — a sandboxed shell command runner allowing the proposer agent to run read-only commands (`grep`, `cat`, etc.) against the trace directory. Disabled by default.
2. **`TraceResourceProvider`** — an MCP resource provider exposing trace files at `trace://{optimizationRunId}/{relativePath}` URIs. Disabled by flag.

Both are security-sensitive: path traversal protection, allowlist enforcement, and auth checks are non-negotiable.

## Dependencies

This section depends on:
- **section-01-config** (`MetaHarnessConfig` with `EnableShellTool`, `EnableMcpTraceResources`, `TraceDirectoryRoot`)
- **section-04-trace-infrastructure** (`IExecutionTraceStore`, `TraceScope`, directory layout)

These sections must be complete before implementing section 13.

## Files Created

```
src/Content/Domain/Domain.AI/MCP/McpRequestContext.cs          — auth principal wrapper (new, needed by interface)
src/Content/Domain/Domain.AI/MCP/McpResource.cs                — MCP resource descriptor record (new)
src/Content/Domain/Domain.AI/MCP/McpResourceContent.cs         — MCP resource content record (new)
src/Content/Application/Application.AI.Common/Interfaces/IMcpResourceProvider.cs  — interface (new, no prior pattern)
src/Content/Infrastructure/Infrastructure.AI/Tools/RestrictedSearchTool.cs
src/Content/Infrastructure/Infrastructure.AI.MCP/Resources/TraceResourceProvider.cs
```

## Files to Modify

```
src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs         — register RestrictedSearchTool (conditional on EnableShellTool)
src/Content/Infrastructure/Infrastructure.AI.MCP/DependencyInjection.cs     — register TraceResourceProvider (conditional on EnableMcpTraceResources)
```

## Test Files Created

```
src/Content/Tests/Infrastructure.AI.Tests/Tools/RestrictedSearchToolTests.cs   — 12 tests (20 total with platform guards)
src/Content/Tests/Infrastructure.AI.Tests/MCP/TraceResourceProviderTests.cs    — 8 tests
```

**Test project updated:** `Infrastructure.AI.Tests.csproj` — added `ProjectReference` for `Infrastructure.AI.MCP`.

## Deviations from Plan

1. **`IAgentTool`/`JsonElement` stub replaced with `ITool`** — spec stub used `IAgentTool` but the actual project uses `ITool` (operation + dictionary pattern). Followed existing codebase conventions.
2. **`IExecutionTraceStore` dropped from `TraceResourceProvider` constructor** — spec included it but the implementation uses `Path.Combine` directly (same logic as `TraceScope.ResolveDirectory`); the store's API is write-oriented and not needed for reads.
3. **New domain/interface types added** — `IMcpResourceProvider`, `McpRequestContext`, `McpResource`, `McpResourceContent` created from scratch (no prior MCP resource provider pattern existed in the project).
4. **Configurable timeout on `RestrictedSearchTool`** — added `TimeSpan? commandTimeout` constructor parameter (default 30s) for test isolation.
5. **Post-review fixes** — stderr capped at 64KB; `ReadAsync` when disabled throws `FileNotFoundException` (consistent with `ListAsync` returning empty).

## Final Test Count: 20 tests, all passing

---

## Tests First

### `RestrictedSearchToolTests.cs`

**Test project:** `Infrastructure.AI.Tests`

Write all tests before implementing. Each test should arrange a temp directory as the fake trace root, construct `RestrictedSearchTool` with that root path, and call `ExecuteAsync`.

```
Execute_Grep_WithinTraceRoot_Succeeds
Execute_Cat_WithinTraceRoot_Succeeds
Execute_Curl_RejectsNonAllowlistedBinary
Execute_Python_RejectsNonAllowlistedBinary
Execute_CommandWithPipe_RejectsMetacharacter
Execute_CommandWithSemicolon_RejectsMetacharacter
Execute_CommandWithRedirect_RejectsMetacharacter
Execute_PathOutsideTraceRoot_Rejects
Execute_PathWithDotDot_RejectsAfterResolution
Execute_SymlinkOutsideRoot_Rejects
Execute_LongRunningCommand_TimesOutAfter30Seconds
Execute_LargeOutput_TruncatesAt1MB
```

Stub shape (fill in assertions per test):

```csharp
public class RestrictedSearchToolTests
{
    private readonly string _traceRoot;

    public RestrictedSearchToolTests()
    {
        _traceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_traceRoot);
    }

    [Fact]
    public async Task Execute_Grep_WithinTraceRoot_Succeeds() { /* ... */ }

    [Fact]
    public async Task Execute_Curl_RejectsNonAllowlistedBinary() { /* ... */ }

    [Fact]
    public async Task Execute_CommandWithPipe_RejectsMetacharacter() { /* ... */ }

    [Fact]
    public async Task Execute_PathWithDotDot_RejectsAfterResolution() { /* ... */ }

    [Fact]
    public async Task Execute_LongRunningCommand_TimesOutAfter30Seconds() { /* ... */ }

    [Fact]
    public async Task Execute_LargeOutput_TruncatesAt1MB() { /* ... */ }
}
```

For the timeout test, use a command that sleeps longer than 30s and verify the result indicates timeout. For the output cap test, write a file containing >1 MB of data and cat it.

### `TraceResourceProviderTests.cs`

**Test project:** `Infrastructure.AI.Tests`

```
Read_ValidPath_ReturnsFileContent
Read_PathWithDotDot_RejectsTraversal
Read_SymlinkOutsideRoot_Rejects
Read_WithoutAuth_Rejects            (expects 401 / auth exception)
List_ValidOptimizationRunId_ReturnsFiles
Read_PathOutsideOptimizationRunDir_Rejects
```

Auth tests should pass a context without a valid JWT and assert an `UnauthorizedException` (or the project's equivalent) is thrown. Path tests construct URIs with `..` segments and assert rejection.

---

## Implementation

### `RestrictedSearchTool`

**File:** `src/Content/Infrastructure/Infrastructure.AI/Tools/RestrictedSearchTool.cs`

Keyed DI name: `"restricted_search"`.

**Constructor parameters:** `IOptions<MetaHarnessConfig>` (to read `TraceDirectoryRoot`), `ILogger<RestrictedSearchTool>`.

**Tool schema:**
- `command` (string, required) — the full shell command string, e.g. `grep -r "error" ./run-abc/`
- `working_directory` (string, optional) — defaults to `{TraceDirectoryRoot}`

**Execution pipeline (in order — fail fast at each step):**

1. **Binary allowlist check.** Extract the first whitespace-delimited token from `command`. Reject immediately if not in:
   `{ "grep", "rg", "cat", "find", "ls", "head", "tail", "jq", "wc" }`
   Return an error result string (not throw) describing what was rejected.

2. **Metacharacter rejection.** Scan the full `command` string for any of:
   `;`, `|`, `&&`, `||`, `>`, `<`, `` ` ``, `$(`, `\n`
   Reject the entire command if any are found.

3. **Working directory path validation.**
   - Call `Path.GetFullPath(working_directory)` to resolve any `..` segments.
   - Verify the resolved path starts with `Path.GetFullPath(TraceDirectoryRoot)`.
   - On non-Windows: also check the resolved path does not follow a symlink pointing outside the root (use `FileInfo.LinkTarget` or `Directory.ResolveLinkTarget`).
   - Reject if either check fails.

4. **Process execution.**
   - `Process.Start` with `UseShellExecute = false`, `RedirectStandardOutput = true`, `RedirectStandardError = true`, `WorkingDirectory = resolvedDirectory`.
   - Do NOT inherit the parent process environment — set `EnvironmentVariables` explicitly to an empty set or a minimal safe set.
   - Wait with a 30-second `CancellationToken`; on timeout kill the process and return a timeout error string.

5. **Output cap.** Read stdout up to 1 MB (1,048,576 bytes). If truncated, append a `\n[output truncated at 1MB]` marker.

6. **Return.** Return the captured stdout (plus truncation marker if needed) as the tool result string. On non-zero exit code, prepend stderr to the result.

**Important:** This tool is only added to the proposer's tool set when `MetaHarnessConfig.EnableShellTool == true`. The tool class itself should always be registered; the guard lives in the proposer context construction, not the DI registration. See section-11-proposer for where the tool set is assembled.

**Stub signature:**

```csharp
/// <summary>
/// Executes read-only shell commands (grep, cat, find, etc.) sandboxed to the trace root.
/// Only available when <see cref="MetaHarnessConfig.EnableShellTool"/> is true.
/// Keyed as <c>"restricted_search"</c>.
/// </summary>
public sealed class RestrictedSearchTool : IAgentTool
{
    private static readonly IReadOnlySet<string> AllowedBinaries = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "grep", "rg", "cat", "find", "ls", "head", "tail", "jq", "wc"
    };

    private static readonly string[] ForbiddenMetacharacters = [";", "|", "&&", "||", ">", "<", "`", "$(", "\n"];

    public RestrictedSearchTool(IOptions<MetaHarnessConfig> config, ILogger<RestrictedSearchTool> logger) { }

    public string Name => "restricted_search";

    public Task<string> ExecuteAsync(JsonElement parameters, CancellationToken cancellationToken) { }

    private static bool IsPathSafe(string resolvedPath, string resolvedRoot) { }
}
```

---

### `TraceResourceProvider`

**File:** `src/Content/Infrastructure/Infrastructure.AI.MCP/Resources/TraceResourceProvider.cs`

This is an MCP resource provider. Follow the existing MCP resource provider pattern already in the project (check `Infrastructure.AI.MCP/` for existing providers as reference).

**Resource URI scheme:** `trace://{optimizationRunId}/{relativePath}`

**Operations:**

- **List** (`trace://{optimizationRunId}/`) — enumerate all files under `{TraceDirectoryRoot}/optimizations/{optimizationRunId}/`. Return each as an MCP resource descriptor with its URI.
- **Read** (`trace://{optimizationRunId}/{relativePath}`) — return the file content as a text resource.

**Security checks (apply to every request, in this order):**

1. **Auth check.** Verify JWT auth via the existing auth middleware/service. Reject with 401 if not authenticated.
2. **Path resolution.** Resolve `{TraceDirectoryRoot}/optimizations/{optimizationRunId}/{relativePath}` via `Path.GetFullPath()`.
3. **Traversal guard.** Reject any `relativePath` containing `..` segments (check before and after resolution).
4. **Root containment.** Verify the resolved absolute path starts with `Path.GetFullPath({TraceDirectoryRoot}/optimizations/{optimizationRunId}/`).
5. **Symlink guard.** On non-Windows platforms, verify the real path does not escape the run directory.

Gate the entire provider behind `MetaHarnessConfig.EnableMcpTraceResources`. If the flag is false, List and Read operations should return empty/not-found rather than throwing.

**Stub signature:**

```csharp
/// <summary>
/// Exposes optimization run trace files as MCP resources at <c>trace://{optimizationRunId}/{path}</c>.
/// Requires JWT authentication. Gated by <see cref="MetaHarnessConfig.EnableMcpTraceResources"/>.
/// </summary>
public sealed class TraceResourceProvider : IMcpResourceProvider
{
    public TraceResourceProvider(
        IOptions<MetaHarnessConfig> config,
        IExecutionTraceStore traceStore,
        ILogger<TraceResourceProvider> logger) { }

    public Task<IReadOnlyList<McpResource>> ListAsync(string uri, McpRequestContext context, CancellationToken ct) { }

    public Task<McpResourceContent> ReadAsync(string uri, McpRequestContext context, CancellationToken ct) { }

    private static bool ValidatePath(string resolvedPath, string runRoot) { }
}
```

---

## DI Registration

### `Infrastructure.AI/DependencyInjection.cs`

Add `RestrictedSearchTool` as a keyed singleton regardless of `EnableShellTool` — the flag controls whether the proposer includes it in the active tool set, not whether it is registered:

```csharp
services.AddKeyedSingleton<IAgentTool, RestrictedSearchTool>("restricted_search");
```

### `Infrastructure.AI.MCP/DependencyInjection.cs`

Register `TraceResourceProvider` as a singleton and add it to the MCP resource provider collection:

```csharp
services.AddSingleton<TraceResourceProvider>();
services.AddSingleton<IMcpResourceProvider>(sp => sp.GetRequiredService<TraceResourceProvider>());
```

---

## Security Notes

Both implementations are security-critical. Before marking this section done:

- Verify `Path.GetFullPath` is called on every user-supplied path before any containment check.
- Verify the containment check uses `StartsWith` on the fully-resolved absolute paths (with trailing separator to avoid prefix false positives, e.g. `/traces/run-1` vs `/traces/run-10`).
- Verify `UseShellExecute = false` on `Process.Start` — shell=true allows metacharacter injection regardless of the earlier string check.
- Verify environment isolation on `Process.Start`.
- Run `/code-review` after implementation per the review cadence rules before proceeding to section-14-outer-loop.
