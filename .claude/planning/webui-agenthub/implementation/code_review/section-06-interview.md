# Section 06 — Code Review Interview Transcript

**Date**: 2026-04-15

---

## Findings Triaged

### Asked User

**HIGH-02: Full tool enumeration on every invoke request**
- Q: GetAllToolsAsync() hits every MCP server on every POST /api/mcp/tools/{name}/invoke. How to fix?
- A: Add `GetToolByNameAsync` to `IMcpToolProvider` (early-exit iteration)
- Applied: Yes — added to interface + McpToolProvider implementation; InvokeTool now calls GetToolByNameAsync

---

### Auto-Fixed

**HIGH-01: Exception message leaked verbatim to HTTP response**
- Risk: ex.Message may contain file paths, connection strings, stack fragments
- Fix: inject IHostEnvironment; gate on IsDevelopment() — detailed in dev, generic in prod
- Applied: Yes — `Error = _environment.IsDevelopment() ? ex.Message : "Tool execution failed. Check server logs."`

**MEDIUM-02: Arguments JsonElement undefined → GetRawText() throws**
- Risk: Client omitting Arguments body causes unhandled 500
- Fix: Guard at top of InvokeTool: `if (request.Arguments.ValueKind == JsonValueKind.Undefined) return BadRequest(...)`
- Applied: Yes

**MEDIUM-04: Duplicated LINQ flatten chain**
- Fix: Extracted `private static FlattenTools(Dictionary<string, IList<AITool>>)` helper used by GetTools and InvokeTool
- Applied: Yes

**MEDIUM-05: Service locator anti-pattern for IMcpPromptProvider**
- Fix: Created NullMcpPromptProvider in Presentation.AgentHub/Services/, registered via TryAddSingleton<IMcpPromptProvider, NullMcpPromptProvider>(), injected directly in constructor
- Applied: Yes — IServiceProvider removed from controller constructor

---

### Let Go

- **MEDIUM-01**: Route constraint `{name:maxlength(128)}` — minor, low risk for POC
- **MEDIUM-03**: LogLevel guard for SHA-256 hashing — premature optimization; hash is cheap vs. network calls
- **LOW-01**: Stopwatch fully-qualified namespace — using directive added via auto-fix
- **LOW-02**: Stub tests with `await Task.CompletedTask` — intentional per section design (completed in section-07)
- **LOW-03**: `Output` as nullable `JsonElement?` — trade-off: nullable adds complexity, default JsonElement is valid JSON null

---

## Files Modified by Fixes

- `Application.AI.Common/Interfaces/IMcpToolProvider.cs` — added `GetToolByNameAsync`
- `Infrastructure.AI.MCP/Services/McpToolProvider.cs` — implemented `GetToolByNameAsync` with early-exit iteration
- `Presentation.AgentHub/Controllers/McpController.cs` — HIGH-01, MEDIUM-02, MEDIUM-04, MEDIUM-05
- `Presentation.AgentHub/Services/NullMcpPromptProvider.cs` — new null-object implementation
- `Presentation.AgentHub/DependencyInjection.cs` — TryAddSingleton registration
