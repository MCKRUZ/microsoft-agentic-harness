# Section 06 -- MCP API Code Review

**Reviewer**: claude-code-reviewer
**Date**: 2026-04-15
**Verdict**: WARNING -- No CRITICAL issues. Two HIGH issues that should be fixed before merge. Several MEDIUM items worth addressing.

---

## HIGH Issues

### [HIGH-01] Exception message leaked verbatim to HTTP response
**File**: Presentation.AgentHub/Controllers/McpController.cs (InvokeTool catch block, line ~163)

The catch block returns ex.Message directly in McpToolInvokeResponse.Error. Exception messages may contain internal details: file paths, connection strings, stack fragments. The XML doc says sanitized error message but no sanitization occurs.

**Impact**: Information disclosure via triggered exceptions.

**Fix**: Return a generic message. Gate on IHostEnvironment.IsDevelopment() for detailed errors in dev only.

```csharp
Error = env.IsDevelopment() ? ex.Message : "Tool execution failed. Check server logs.",
```

---

### [HIGH-02] Tool lookup fetches ALL tools from ALL MCP servers on every invoke request
**File**: Presentation.AgentHub/Controllers/McpController.cs (InvokeTool, lines ~110-116)

GetAllToolsAsync() is called on every POST to /api/mcp/tools/{name}/invoke. O(servers * tools) network calls for a single tool lookup.

**Impact**: Performance degradation under concurrent invocations.

**Fix (short-term)**: Add GetToolByNameAsync or cache GetAllToolsAsync with short TTL.

**Fix (longer-term)**: Move tool resolution to a mediator command (InvokeToolCommand).

---

## MEDIUM Issues

### [MEDIUM-01] Tool name route parameter is not validated
**File**: Presentation.AgentHub/Controllers/McpController.cs (InvokeTool, line ~108)

No validation that name conforms to expected patterns. A very long name flows through GetAllToolsAsync before returning null.

**Fix**: Add route constraint: tools/{name:maxlength(128)}/invoke

---

### [MEDIUM-02] Arguments JsonElement default value is undefined -- missing validation
**File**: Presentation.AgentHub/Models/McpToolInvokeRequest.cs:12

Arguments is a JsonElement struct with no required keyword. default(JsonElement) causes GetRawText() to throw InvalidOperationException -- unhandled 500.

**Fix**: Guard: if (request.Arguments.ValueKind == JsonValueKind.Undefined) return BadRequest(...)

---

### [MEDIUM-03] SHA-256 hashing runs even when Information logging is disabled
**File**: Presentation.AgentHub/Controllers/McpController.cs (lines ~119-124)

SHA256.HashData() + GetRawText() + Convert.ToHexString() runs unconditionally. Wasted CPU when logging suppressed.

**Fix**: Guard with _logger.IsEnabled(LogLevel.Information).

---

### [MEDIUM-04] Duplicated LINQ chain in GetTools and InvokeTool
**File**: Presentation.AgentHub/Controllers/McpController.cs (lines ~93-104 and ~111-116)

Both use allTools.Values.SelectMany(...).OfType<AIFunction>(). Change in one must be mirrored in other.

**Fix**: Extract private static FlattenTools helper.

---

### [MEDIUM-05] IMcpPromptProvider resolved via IServiceProvider -- service locator anti-pattern
**File**: Presentation.AgentHub/Controllers/McpController.cs (lines ~84-85)

Hides dependency, makes testing harder. ASP.NET Core DI throws on unregistered constructor params.

**Fix**: Register NullMcpPromptProvider via TryAddSingleton and inject IMcpPromptProvider directly.

---

## LOW Issues

### [LOW-01] Stopwatch uses fully-qualified namespace
**File**: Presentation.AgentHub/Controllers/McpController.cs (line ~138)

Minor readability issue. Consider TimeProvider for testability.

---

### [LOW-02] Tests are all stubs with no assertions
**File**: Presentation.AgentHub.Tests/Controllers/McpControllerTests.cs (entire file)

All 9 tests are await Task.CompletedTask stubs. Consider [Fact(Skip)] so CI reports them as skipped.

---

### [LOW-03] McpToolInvokeResponse.Output default is undefined JsonElement
**File**: Presentation.AgentHub/Models/McpToolInvokeResponse.cs:8

When Success=false, Output is default(JsonElement). Consider making it nullable: JsonElement? Output.

---

## INFO

### [INFO-01] Clean Architecture compliance is good
- McpPrompt in Domain.AI/MCP/ with zero framework deps. IMcpPromptProvider in Application.AI.Common/Interfaces/. DTOs in Presentation.AgentHub/Models/. Dependency direction correct.

### [INFO-02] Security posture is solid for a POC
- [Authorize] on controller. [RequestSizeLimit(32KB)]. [EnableRateLimiting] on invoke. SHA-256 audit hashing. McpRequestContext.FromPrincipal(User) for auth context.

### [INFO-03] HTTP 200 for tool failures is correct
Matches MCP protocol semantics -- tool errors are application-level, not transport-level.

### [INFO-04] Immutability conventions followed
All DTOs use sealed record with init-only properties. No mutation patterns.

---

## Summary

| Severity | Count | Action Required |
|----------|-------|-----------------|
| CRITICAL | 0 | -- |
| HIGH | 2 | Must fix before merge |
| MEDIUM | 5 | Should fix |
| LOW | 3 | Consider improving |
| INFO | 4 | No action needed |

**Verdict: WARNING** -- The two HIGH issues (exception message leakage and per-request full tool enumeration) should be addressed before merge. MEDIUM-02 and MEDIUM-05 are worth fixing in this pass.
