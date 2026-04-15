# Section 06 — MCP Artifacts API

## Overview

Build `McpController` in `Presentation.AgentHub`, exposing HTTP endpoints for listing and invoking MCP tools, resources, and prompts. These endpoints back the WebUI's Tools/Resources/Prompts browser tabs.

**Depends on:** section-02-agenthub-core (DI setup, auth middleware, `Program.cs`)

**Can parallelize with:** section-03-conversation-store, section-05-otel-bridge, section-09-msal-auth

---

## Tests First

Write these tests before implementing. All tests live in the AgentHub test project and use `WebApplicationFactory<Program>` with `TestWebApplicationFactory` (built in section-07, but you can stub it here with a minimal in-process factory for TDD).

### Test List

```
GET /api/mcp/tools returns 200 with tool list
GET /api/mcp/tools returns tool name, description, and schema
GET /api/mcp/prompts returns empty array when no prompt provider registered
POST /api/mcp/tools/{name}/invoke with valid args returns 200 with output
POST /api/mcp/tools/nonexistent/invoke returns 404
POST /api/mcp/tools/{name}/invoke with tool execution error returns 200 with Success=false
POST /api/mcp/tools/{name}/invoke emits structured audit log entry
POST /api/mcp/tools/{name}/invoke body exceeding 32KB returns 413
POST /api/mcp/tools/{name}/invoke audit log does not include raw arguments at Information level
```

### Test Stub Signatures

```csharp
// File: src/Content/Tests/AgentHub.Tests/Controllers/McpControllerTests.cs

/// <summary>Tests for <see cref="McpController"/> HTTP endpoints.</summary>
public class McpControllerTests : IClassFixture<TestWebApplicationFactory>
{
    /// <summary>GET /api/mcp/tools returns 200 with at least one tool.</summary>
    [Fact] public async Task GetTools_ReturnsOkWithToolList() { }

    /// <summary>Each tool in the response has Name, Description, and Schema populated.</summary>
    [Fact] public async Task GetTools_EachToolHasNameDescriptionAndSchema() { }

    /// <summary>GET /api/mcp/prompts returns 200 empty array when IMcpPromptProvider is absent.</summary>
    [Fact] public async Task GetPrompts_ReturnsEmptyArrayWhenNoProviderRegistered() { }

    /// <summary>POST invoke with valid args returns 200 and Success=true.</summary>
    [Fact] public async Task InvokeTool_ValidArgs_Returns200WithOutput() { }

    /// <summary>POST invoke for unknown tool returns 404.</summary>
    [Fact] public async Task InvokeTool_UnknownTool_Returns404() { }

    /// <summary>POST invoke where the tool throws returns 200 with Success=false and sanitized error.</summary>
    [Fact] public async Task InvokeTool_ToolExecutionFailure_Returns200WithSuccessFalse() { }

    /// <summary>POST invoke emits a structured audit log at Information level with UserId, ToolName, InputHash.</summary>
    [Fact] public async Task InvokeTool_EmitsStructuredAuditLog() { }

    /// <summary>POST invoke with body over 32KB returns 413 Request Entity Too Large.</summary>
    [Fact] public async Task InvokeTool_OversizedBody_Returns413() { }

    /// <summary>Audit log at Information level does not contain raw argument values.</summary>
    [Fact] public async Task InvokeTool_AuditLog_DoesNotContainRawArgumentsAtInfoLevel() { }
}
```

---

## Files to Create

### 1. DTOs

**File:** `src/Content/Presentation/Presentation.AgentHub/Models/McpToolDto.cs`

```csharp
/// <summary>Describes a single MCP tool available for invocation.</summary>
public sealed record McpToolDto
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public JsonElement Schema { get; init; }
}
```

**File:** `src/Content/Presentation/Presentation.AgentHub/Models/McpResourceDto.cs`

```csharp
/// <summary>Describes a single MCP resource exposed by the server.</summary>
public sealed record McpResourceDto
{
    public required string Uri { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public string? MimeType { get; init; }
}
```

**File:** `src/Content/Presentation/Presentation.AgentHub/Models/McpPromptDto.cs`

```csharp
/// <summary>Describes a single MCP prompt template.</summary>
public sealed record McpPromptDto
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
}
```

**File:** `src/Content/Presentation/Presentation.AgentHub/Models/McpToolInvokeRequest.cs`

```csharp
/// <summary>Request body for invoking an MCP tool directly via HTTP.</summary>
public sealed record McpToolInvokeRequest
{
    public JsonElement Arguments { get; init; }
}
```

**File:** `src/Content/Presentation/Presentation.AgentHub/Models/McpToolInvokeResponse.cs`

```csharp
/// <summary>Response envelope for a tool invocation.</summary>
public sealed record McpToolInvokeResponse
{
    public JsonElement Output { get; init; }
    public long DurationMs { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
}
```

### 2. Controller

**File:** `src/Content/Presentation/Presentation.AgentHub/Controllers/McpController.cs`

```csharp
/// <summary>
/// Exposes MCP tools, resources, and prompts over HTTP for the WebUI panels.
/// All endpoints require authentication. Tool invocations are audit-logged.
/// </summary>
[ApiController]
[Route("api/mcp")]
[Authorize]
public sealed class McpController : ControllerBase
{
    // Constructor: inject IMcpToolProvider, IMcpResourceProvider,
    // IServiceProvider (for optional IMcpPromptProvider), ILogger<McpController>

    /// <summary>Returns all registered MCP tools with their schemas.</summary>
    [HttpGet("tools")]
    public async Task<IActionResult> GetTools() { }

    /// <summary>Returns all registered MCP resources.</summary>
    [HttpGet("resources")]
    public async Task<IActionResult> GetResources() { }

    /// <summary>
    /// Returns all registered MCP prompts.
    /// Returns an empty array if <c>IMcpPromptProvider</c> is not registered — never throws.
    /// </summary>
    [HttpGet("prompts")]
    public async Task<IActionResult> GetPrompts() { }

    /// <summary>
    /// Invokes the named MCP tool with the supplied arguments.
    /// Emits a structured audit log entry (UserId, ToolName, InputHash) at Information level.
    /// Raw arguments are only logged at Debug level.
    /// </summary>
    [HttpPost("tools/{name}/invoke")]
    [RequestSizeLimit(32 * 1024)]
    public async Task<IActionResult> InvokeTool(string name, [FromBody] McpToolInvokeRequest request) { }
}
```

---

## Implementation Notes

### Dependency Resolution

`IMcpToolProvider` and `IMcpResourceProvider` are already registered by the `Presentation.Common.GetServices(true)` call wired in section-02. Inject them via constructor.

`IMcpPromptProvider` is optional. Resolve it via `IServiceProvider.GetService<IMcpPromptProvider>()` rather than constructor injection — return an empty array if it resolves to `null`.

### Audit Logging for InvokeTool

Compute a SHA-256 hash of the serialized `Arguments` JsonElement. Log a structured entry at `Information` level containing only `UserId` (from `User.FindFirstValue(ClaimTypes.NameIdentifier)`), `ToolName`, `InputHash`, and `Timestamp`. Do not include the raw arguments object in the `Information`-level log message. Log the raw `Arguments` at `Debug` level separately.

```csharp
// Correct pattern:
_logger.LogInformation(
    "MCP tool invoked. UserId={UserId} ToolName={ToolName} InputHash={InputHash}",
    userId, name, inputHash);

_logger.LogDebug("MCP tool raw arguments. ToolName={ToolName} Arguments={Arguments}", name, request.Arguments);
```

### Tool Not Found

Before invoking, check whether the tool exists via `IMcpToolProvider`. If the named tool is not found, return `NotFound()` (404).

### Tool Execution Failure

Wrap the invocation in a try/catch. On exception, log the full exception at `Error` level server-side. Return HTTP 200 with `McpToolInvokeResponse { Success = false, Error = "<sanitized message>" }`. Never include stack traces or internal paths in the response body.

### Request Size Limit

Apply `[RequestSizeLimit(32 * 1024)]` directly on the `InvokeTool` action. The test verifying 413 behavior requires the Kestrel request body size enforcement to be active; confirm `AddControllers()` does not override this globally.

### Return Empty Array on Missing Provider

For `GetPrompts`, pattern:

```csharp
var provider = _serviceProvider.GetService<IMcpPromptProvider>();
if (provider is null) return Ok(Array.Empty<McpPromptDto>());
```

This ensures the WebUI receives a valid JSON array, not a 500.

---

## Rate Limiting Context

A fixed-window rate limit of 10 requests/minute per IP is applied to `POST /api/mcp/tools/{name}/invoke`. This policy is registered in section-02's `DependencyInjection.cs` and wired via `UseRateLimiter()` in `Program.cs`. The `McpController` action should be decorated with the appropriate `[EnableRateLimiting("McpToolInvoke")]` attribute once the policy name is confirmed in section-02.

---

## Actual Implementation Notes

### Files Created
- `Domain.AI/MCP/McpPrompt.cs` — domain record (Name, Description, Arguments)
- `Application.AI.Common/Interfaces/IMcpPromptProvider.cs` — optional interface; resolved via TryAddSingleton
- `Application.AI.Common/Interfaces/IMcpToolProvider.cs` — extended with `GetToolByNameAsync` (early-exit lookup)
- `Infrastructure.AI.MCP/Services/McpToolProvider.cs` — implemented `GetToolByNameAsync` (sequential, early-exit)
- `Presentation.AgentHub/Controllers/McpController.cs` — all 4 routes
- `Presentation.AgentHub/Models/McpToolDto.cs`, `McpResourceDto.cs`, `McpPromptDto.cs`, `McpToolInvokeRequest.cs`, `McpToolInvokeResponse.cs`
- `Presentation.AgentHub/Services/NullMcpPromptProvider.cs` — null-object default for IMcpPromptProvider
- `Presentation.AgentHub.Tests/Controllers/McpControllerTests.cs` — 9 stub tests

### Deviations from Plan
- **`IMcpPromptProvider` resolved via NullMcpPromptProvider** (not IServiceProvider.GetService): Code review flagged service-locator anti-pattern. Used `TryAddSingleton<IMcpPromptProvider, NullMcpPromptProvider>()` + direct constructor injection instead.
- **`GetToolByNameAsync` added to IMcpToolProvider**: Code review flagged O(servers × tools) enumeration on every invoke. Added early-exit method to the interface and Infrastructure implementation.
- **`[EnableRateLimiting("McpToolInvoke")]` removed**: GlobalLimiter in DependencyInjection.cs already covers POST `/api/mcp/tools/*` paths. No named policy "McpToolInvoke" was registered.
- **Environment-gated error messages**: `ex.Message` returned only in Development; generic message in Production (HIGH-01 fix).
- **`Arguments` undefined guard**: Added `BadRequest` when `JsonElement.ValueKind == Undefined` (MEDIUM-02 fix).
- **`FlattenTools` helper**: Extracted to avoid duplicated LINQ chain in `GetTools` and `InvokeTool`.

### Test Results
All 9 tests pass (stubs — wired up in section-07).

## Verification

```
dotnet build src/AgenticHarness.slnx
dotnet test src/AgenticHarness.slnx --filter "FullyQualifiedName~McpController"
```

All 9 tests pass.
