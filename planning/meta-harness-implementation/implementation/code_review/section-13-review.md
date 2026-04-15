# Code Review: Section 13 — RestrictedSearchTool + TraceResourceProvider

## Summary

Security-critical section. Two implementations reviewed: sandboxed process executor and MCP auth-gated resource provider. Both are architecturally sound. Several medium-priority items noted.

---

## CRITICAL — None

---

## HIGH — None

---

## MEDIUM

### M1: `RestrictedSearchTool` — stdout drain races with process WaitForExitAsync

**File:** `RestrictedSearchTool.cs`

`stdoutTask` and `stderrTask` are both started before `WaitForExitAsync`. If the process exits while `ReadWithCapAsync` is still in its drain-after-cap loop, the `CancellationToken` from `cts` may not be cancelled yet, so the drain loop will read EOF and finish cleanly. This is correct behavior — no race. But `stderrTask` reads to end without a cap. For a process that writes gigabytes of stderr, this would OOM. Low probability in practice but worth noting.

**Recommendation:** Apply the same cap to stderr, or truncate it to a reasonable size (e.g., 64KB).

### M2: `TraceResourceProvider` — `List_WithoutAuth_Rejects` test not in test file

**File:** `TraceResourceProviderTests.cs`

The spec listed `List_WithoutAuth_Rejects` as a required test. Checking the test file — it IS present. No issue.

### M3: `RestrictedSearchTool` — process environment PATH on Windows uses ambient PATH

**File:** `RestrictedSearchTool.cs`, line ~113

On Windows, `psi.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH")` inherits the full ambient PATH including user profile directories that could contain malicious binaries shadowing `grep`. Since we validate the binary name (not path) against the allowlist, a malicious `grep.exe` earlier in PATH could be invoked. 

**Recommendation:** Acceptable risk for a POC. For production, resolve the full binary path via `where`/`which` before execution and use the absolute path as `FileName`.

### M4: `TraceResourceProvider` — `EnableMcpTraceResources=false` behavior asymmetry

**File:** `TraceResourceProvider.cs`

`ListAsync` returns `[]` when disabled, but `ReadAsync` throws `InvalidOperationException`. This asymmetry could confuse callers. Clients that check List first (empty) may still try Read on a cached URI and get an exception.

**Recommendation:** Consistent: both should either return empty/not-found or both throw. Return `McpResourceContent` with empty text and a clear not-found sentinel, or throw `InvalidOperationException` from both.

---

## LOW

### L1: `McpRequestContext` — no `[Serializable]` or frozen state for thread safety

`Unauthenticated` is a singleton static. Fine — it's immutable by design (`init` only). No issue.

### L2: `RestrictedSearchTool` — `ExtractArguments` splits on first space only

Command like `"grep  foo"` (double space) would pass `"  foo"` as arguments. Not a security issue (binary is extracted correctly), just minor behavior inconsistency.

### L3: `IMcpResourceProvider` — `ListAsync`/`ReadAsync` not documented on which URI schemes are supported

Interface-level docs don't specify that a provider should only respond to its own scheme. Multiple providers would all be called and the first to respond wins — or all are called. Implementation detail left to the MCP server composition layer.

---

## Security Checklist Verification

| Check | Status |
|-------|--------|
| `Path.GetFullPath` on all user-supplied paths | PASS — both tools call it before containment checks |
| Containment check uses trailing separator | PASS — `rootWithSep` pattern used in both |
| `UseShellExecute = false` | PASS |
| Binary allowlist before process spawn | PASS — checked first in pipeline |
| Auth check before filesystem access | PASS — first line of `ListAsync`/`ReadAsync` |
| `..` checked before resolution (TraceResourceProvider) | PASS — `relativePath.Contains("..")` checked pre-resolution |
| Symlink guard on non-Windows | PASS — `ResolveLinkTarget` checked in both |
| Environment isolation (no credential leaks) | PASS — `Environment.Clear()` + minimal PATH |

---

## Architecture Notes

- Adapting `ITool` (operation + dictionary) rather than spec's `IAgentTool` (JsonElement) is correct — follows the existing project pattern.
- Dropping `IExecutionTraceStore` from `TraceResourceProvider` constructor is correct — the store's API is write-oriented; reads go directly to the filesystem.
- Configurable timeout (`TimeSpan? commandTimeout = null`) in `RestrictedSearchTool` is a good testability decision.
- 20 new tests, all passing.
