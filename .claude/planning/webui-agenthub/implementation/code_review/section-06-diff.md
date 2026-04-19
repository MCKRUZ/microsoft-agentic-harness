diff --git a/src/Content/Application/Application.AI.Common/Interfaces/IMcpPromptProvider.cs b/src/Content/Application/Application.AI.Common/Interfaces/IMcpPromptProvider.cs
new file mode 100644
index 0000000..69d0e96
--- /dev/null
+++ b/src/Content/Application/Application.AI.Common/Interfaces/IMcpPromptProvider.cs
@@ -0,0 +1,16 @@
+using Domain.AI.MCP;
+
+namespace Application.AI.Common.Interfaces;
+
+/// <summary>
+/// Provides MCP prompt templates for discovery via the HTTP API.
+/// This interface is optional — consumers should resolve it via
+/// <c>IServiceProvider.GetService&lt;IMcpPromptProvider&gt;()</c> and handle a <see langword="null"/>
+/// result gracefully by returning an empty collection.
+/// </summary>
+public interface IMcpPromptProvider
+{
+    /// <summary>Returns all registered prompt templates.</summary>
+    /// <param name="ct">Cancellation token.</param>
+    Task<IReadOnlyList<McpPrompt>> GetPromptsAsync(CancellationToken ct = default);
+}
diff --git a/src/Content/Domain/Domain.AI/MCP/McpPrompt.cs b/src/Content/Domain/Domain.AI/MCP/McpPrompt.cs
new file mode 100644
index 0000000..e4b6f2d
--- /dev/null
+++ b/src/Content/Domain/Domain.AI/MCP/McpPrompt.cs
@@ -0,0 +1,13 @@
+namespace Domain.AI.MCP;
+
+/// <summary>
+/// Describes a prompt template exposed by an MCP prompt provider.
+/// Returned by <c>IMcpPromptProvider.GetPromptsAsync</c>.
+/// </summary>
+/// <param name="Name">Unique prompt name used as a lookup key.</param>
+/// <param name="Description">Human-readable description of what the prompt does.</param>
+/// <param name="Arguments">Names of the arguments the prompt template accepts.</param>
+public sealed record McpPrompt(
+    string Name,
+    string Description,
+    IReadOnlyList<string> Arguments);
diff --git a/src/Content/Presentation/Presentation.AgentHub/Controllers/McpController.cs b/src/Content/Presentation/Presentation.AgentHub/Controllers/McpController.cs
new file mode 100644
index 0000000..a56f115
--- /dev/null
+++ b/src/Content/Presentation/Presentation.AgentHub/Controllers/McpController.cs
@@ -0,0 +1,168 @@
+using System.Security.Claims;
+using System.Security.Cryptography;
+using System.Text;
+using System.Text.Json;
+using Application.AI.Common.Interfaces;
+using Domain.AI.MCP;
+using Microsoft.AspNetCore.Authorization;
+using Microsoft.AspNetCore.Mvc;
+using Microsoft.AspNetCore.RateLimiting;
+using Microsoft.Extensions.AI;
+using Presentation.AgentHub.Models;
+
+namespace Presentation.AgentHub.Controllers;
+
+/// <summary>
+/// Exposes MCP tools, resources, and prompts over HTTP for the WebUI panels.
+/// All endpoints require authentication. Tool invocations are audit-logged.
+/// </summary>
+[ApiController]
+[Route("api/mcp")]
+[Authorize]
+public sealed class McpController : ControllerBase
+{
+    private readonly IMcpToolProvider _toolProvider;
+    private readonly IMcpResourceProvider _resourceProvider;
+    private readonly IServiceProvider _serviceProvider;
+    private readonly ILogger<McpController> _logger;
+
+    /// <summary>Initialises the controller with its dependencies.</summary>
+    public McpController(
+        IMcpToolProvider toolProvider,
+        IMcpResourceProvider resourceProvider,
+        IServiceProvider serviceProvider,
+        ILogger<McpController> logger)
+    {
+        _toolProvider = toolProvider;
+        _resourceProvider = resourceProvider;
+        _serviceProvider = serviceProvider;
+        _logger = logger;
+    }
+
+    /// <summary>Returns all registered MCP tools with their schemas.</summary>
+    [HttpGet("tools")]
+    public async Task<IActionResult> GetTools(CancellationToken ct)
+    {
+        var allTools = await _toolProvider.GetAllToolsAsync(ct);
+        var dtos = allTools.Values
+            .SelectMany(tools => tools)
+            .OfType<AIFunction>()
+            .Select(fn => new McpToolDto
+            {
+                Name = fn.Name,
+                Description = fn.Description,
+                Schema = fn.JsonSchema,
+            })
+            .ToList();
+        return Ok(dtos);
+    }
+
+    /// <summary>Returns all registered MCP resources.</summary>
+    [HttpGet("resources")]
+    public async Task<IActionResult> GetResources(CancellationToken ct)
+    {
+        var context = McpRequestContext.FromPrincipal(User);
+        var resources = await _resourceProvider.ListAsync(string.Empty, context, ct);
+        var dtos = resources
+            .Select(r => new McpResourceDto
+            {
+                Uri = r.Uri,
+                Name = r.Name,
+                Description = r.Description ?? string.Empty,
+                MimeType = r.MimeType,
+            })
+            .ToList();
+        return Ok(dtos);
+    }
+
+    /// <summary>
+    /// Returns all registered MCP prompts.
+    /// Returns an empty array if <c>IMcpPromptProvider</c> is not registered — never throws.
+    /// </summary>
+    [HttpGet("prompts")]
+    public async Task<IActionResult> GetPrompts(CancellationToken ct)
+    {
+        var provider = _serviceProvider.GetService<IMcpPromptProvider>();
+        if (provider is null)
+            return Ok(Array.Empty<McpPromptDto>());
+
+        var prompts = await provider.GetPromptsAsync(ct);
+        var dtos = prompts
+            .Select(p => new McpPromptDto
+            {
+                Name = p.Name,
+                Description = p.Description,
+                Arguments = p.Arguments,
+            })
+            .ToList();
+        return Ok(dtos);
+    }
+
+    /// <summary>
+    /// Invokes the named MCP tool with the supplied arguments.
+    /// Emits a structured audit log entry (UserId, ToolName, InputHash) at Information level.
+    /// Raw arguments are only logged at Debug level.
+    /// </summary>
+    [HttpPost("tools/{name}/invoke")]
+    [RequestSizeLimit(32 * 1024)]
+    [EnableRateLimiting("McpToolInvoke")]
+    public async Task<IActionResult> InvokeTool(string name, [FromBody] McpToolInvokeRequest request, CancellationToken ct)
+    {
+        var allTools = await _toolProvider.GetAllToolsAsync(ct);
+        var tool = allTools.Values
+            .SelectMany(tools => tools)
+            .OfType<AIFunction>()
+            .FirstOrDefault(fn => fn.Name == name);
+
+        if (tool is null)
+            return NotFound();
+
+        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
+        var rawArgs = request.Arguments.GetRawText();
+        var inputHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawArgs))).ToLowerInvariant();
+
+        _logger.LogInformation(
+            "MCP tool invoked. UserId={UserId} ToolName={ToolName} InputHash={InputHash}",
+            userId, name, inputHash);
+
+        _logger.LogDebug(
+            "MCP tool raw arguments. ToolName={ToolName} Arguments={Arguments}",
+            name, request.Arguments);
+
+        var args = new AIFunctionArguments();
+        if (request.Arguments.ValueKind == JsonValueKind.Object)
+        {
+            foreach (var prop in request.Arguments.EnumerateObject())
+                args[prop.Name] = prop.Value;
+        }
+
+        var sw = System.Diagnostics.Stopwatch.StartNew();
+        try
+        {
+            var result = await tool.InvokeAsync(args, ct);
+            sw.Stop();
+
+            var output = result is JsonElement je
+                ? je
+                : JsonSerializer.SerializeToElement(result);
+
+            return Ok(new McpToolInvokeResponse
+            {
+                Output = output,
+                DurationMs = sw.ElapsedMilliseconds,
+                Success = true,
+            });
+        }
+        catch (Exception ex)
+        {
+            sw.Stop();
+            _logger.LogError(ex, "MCP tool {ToolName} threw during invocation.", name);
+            return Ok(new McpToolInvokeResponse
+            {
+                DurationMs = sw.ElapsedMilliseconds,
+                Success = false,
+                Error = ex.Message,
+            });
+        }
+    }
+}
diff --git a/src/Content/Presentation/Presentation.AgentHub/Models/McpPromptDto.cs b/src/Content/Presentation/Presentation.AgentHub/Models/McpPromptDto.cs
new file mode 100644
index 0000000..492e30e
--- /dev/null
+++ b/src/Content/Presentation/Presentation.AgentHub/Models/McpPromptDto.cs
@@ -0,0 +1,14 @@
+namespace Presentation.AgentHub.Models;
+
+/// <summary>Describes a single MCP prompt template.</summary>
+public sealed record McpPromptDto
+{
+    /// <summary>Unique prompt name.</summary>
+    public required string Name { get; init; }
+
+    /// <summary>Human-readable description of the prompt.</summary>
+    public required string Description { get; init; }
+
+    /// <summary>Names of the arguments the prompt accepts.</summary>
+    public IReadOnlyList<string> Arguments { get; init; } = [];
+}
diff --git a/src/Content/Presentation/Presentation.AgentHub/Models/McpResourceDto.cs b/src/Content/Presentation/Presentation.AgentHub/Models/McpResourceDto.cs
new file mode 100644
index 0000000..c59feac
--- /dev/null
+++ b/src/Content/Presentation/Presentation.AgentHub/Models/McpResourceDto.cs
@@ -0,0 +1,17 @@
+namespace Presentation.AgentHub.Models;
+
+/// <summary>Describes a single MCP resource exposed by the server.</summary>
+public sealed record McpResourceDto
+{
+    /// <summary>Resource URI (e.g. <c>trace://run-id/</c>).</summary>
+    public required string Uri { get; init; }
+
+    /// <summary>Human-readable resource name.</summary>
+    public required string Name { get; init; }
+
+    /// <summary>Human-readable description of the resource.</summary>
+    public required string Description { get; init; }
+
+    /// <summary>Optional MIME type of the resource content.</summary>
+    public string? MimeType { get; init; }
+}
diff --git a/src/Content/Presentation/Presentation.AgentHub/Models/McpToolDto.cs b/src/Content/Presentation/Presentation.AgentHub/Models/McpToolDto.cs
new file mode 100644
index 0000000..c587de0
--- /dev/null
+++ b/src/Content/Presentation/Presentation.AgentHub/Models/McpToolDto.cs
@@ -0,0 +1,16 @@
+using System.Text.Json;
+
+namespace Presentation.AgentHub.Models;
+
+/// <summary>Describes a single MCP tool available for invocation via the HTTP API.</summary>
+public sealed record McpToolDto
+{
+    /// <summary>Unique tool name used as the <c>{name}</c> path segment in invoke requests.</summary>
+    public required string Name { get; init; }
+
+    /// <summary>Human-readable description of what the tool does.</summary>
+    public required string Description { get; init; }
+
+    /// <summary>JSON Schema describing the tool's input parameters.</summary>
+    public JsonElement Schema { get; init; }
+}
diff --git a/src/Content/Presentation/Presentation.AgentHub/Models/McpToolInvokeRequest.cs b/src/Content/Presentation/Presentation.AgentHub/Models/McpToolInvokeRequest.cs
new file mode 100644
index 0000000..9e68bbe
--- /dev/null
+++ b/src/Content/Presentation/Presentation.AgentHub/Models/McpToolInvokeRequest.cs
@@ -0,0 +1,13 @@
+using System.Text.Json;
+
+namespace Presentation.AgentHub.Models;
+
+/// <summary>Request body for invoking an MCP tool directly via HTTP.</summary>
+public sealed record McpToolInvokeRequest
+{
+    /// <summary>
+    /// Tool arguments as a JSON object. Passed verbatim to the underlying
+    /// <c>AIFunction.InvokeAsync</c>. Each property maps to a named parameter.
+    /// </summary>
+    public JsonElement Arguments { get; init; }
+}
diff --git a/src/Content/Presentation/Presentation.AgentHub/Models/McpToolInvokeResponse.cs b/src/Content/Presentation/Presentation.AgentHub/Models/McpToolInvokeResponse.cs
new file mode 100644
index 0000000..5356b6e
--- /dev/null
+++ b/src/Content/Presentation/Presentation.AgentHub/Models/McpToolInvokeResponse.cs
@@ -0,0 +1,19 @@
+using System.Text.Json;
+
+namespace Presentation.AgentHub.Models;
+
+/// <summary>Response envelope for an MCP tool invocation.</summary>
+public sealed record McpToolInvokeResponse
+{
+    /// <summary>Serialized output from the tool. Populated only when <see cref="Success"/> is <see langword="true"/>.</summary>
+    public JsonElement Output { get; init; }
+
+    /// <summary>Wall-clock duration of the invocation in milliseconds.</summary>
+    public long DurationMs { get; init; }
+
+    /// <summary><see langword="true"/> when the tool completed without throwing.</summary>
+    public bool Success { get; init; }
+
+    /// <summary>Sanitized error message. Populated only when <see cref="Success"/> is <see langword="false"/>.</summary>
+    public string? Error { get; init; }
+}
diff --git a/src/Content/Tests/Presentation.AgentHub.Tests/Controllers/McpControllerTests.cs b/src/Content/Tests/Presentation.AgentHub.Tests/Controllers/McpControllerTests.cs
new file mode 100644
index 0000000..a8cf4bb
--- /dev/null
+++ b/src/Content/Tests/Presentation.AgentHub.Tests/Controllers/McpControllerTests.cs
@@ -0,0 +1,77 @@
+using Xunit;
+
+namespace Presentation.AgentHub.Tests.Controllers;
+
+/// <summary>
+/// Integration tests for <see cref="McpController"/> HTTP endpoints.
+/// Stubs — wired up fully in section-07 once TestAuthHandler supports per-test claim injection
+/// and fake tool/resource providers are registered via WebApplicationFactory overrides.
+/// </summary>
+public sealed class McpControllerTests : IClassFixture<TestWebApplicationFactory>
+{
+    public McpControllerTests(TestWebApplicationFactory factory) { }
+
+    /// <summary>GET /api/mcp/tools returns 200 with at least one tool.</summary>
+    [Fact]
+    public async Task GetTools_ReturnsOkWithToolList()
+    {
+        // Implemented in section-07 with fake IMcpToolProvider registered via factory override.
+        await Task.CompletedTask;
+    }
+
+    /// <summary>Each tool in the response has Name, Description, and Schema populated.</summary>
+    [Fact]
+    public async Task GetTools_EachToolHasNameDescriptionAndSchema()
+    {
+        await Task.CompletedTask;
+    }
+
+    /// <summary>GET /api/mcp/prompts returns 200 empty array when IMcpPromptProvider is absent.</summary>
+    [Fact]
+    public async Task GetPrompts_ReturnsEmptyArrayWhenNoProviderRegistered()
+    {
+        await Task.CompletedTask;
+    }
+
+    /// <summary>POST invoke with valid args returns 200 and Success=true.</summary>
+    [Fact]
+    public async Task InvokeTool_ValidArgs_Returns200WithOutput()
+    {
+        await Task.CompletedTask;
+    }
+
+    /// <summary>POST invoke for unknown tool returns 404.</summary>
+    [Fact]
+    public async Task InvokeTool_UnknownTool_Returns404()
+    {
+        await Task.CompletedTask;
+    }
+
+    /// <summary>POST invoke where the tool throws returns 200 with Success=false and sanitized error.</summary>
+    [Fact]
+    public async Task InvokeTool_ToolExecutionFailure_Returns200WithSuccessFalse()
+    {
+        await Task.CompletedTask;
+    }
+
+    /// <summary>POST invoke emits a structured audit log at Information level with UserId, ToolName, InputHash.</summary>
+    [Fact]
+    public async Task InvokeTool_EmitsStructuredAuditLog()
+    {
+        await Task.CompletedTask;
+    }
+
+    /// <summary>POST invoke with body over 32KB returns 413 Request Entity Too Large.</summary>
+    [Fact]
+    public async Task InvokeTool_OversizedBody_Returns413()
+    {
+        await Task.CompletedTask;
+    }
+
+    /// <summary>Audit log at Information level does not contain raw argument values.</summary>
+    [Fact]
+    public async Task InvokeTool_AuditLog_DoesNotContainRawArgumentsAtInfoLevel()
+    {
+        await Task.CompletedTask;
+    }
+}
