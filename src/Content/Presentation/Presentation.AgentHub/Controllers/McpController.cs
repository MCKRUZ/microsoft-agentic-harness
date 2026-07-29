using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.AI.Common.Interfaces;
using Domain.AI.MCP;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Presentation.AgentHub.DTOs;
using Presentation.Common.Extensions;

namespace Presentation.AgentHub.Controllers;

/// <summary>
/// Exposes MCP tools, resources, and prompts over HTTP for the WebUI panels.
/// All endpoints require authentication. Tool invocations are audit-logged.
/// </summary>
[ApiController]
[Route("api/mcp")]
[Authorize]
public sealed class McpController : ControllerBase
{
    private readonly IMcpToolProvider _toolProvider;
    private readonly IMcpResourceProvider _resourceProvider;
    private readonly IMcpPromptProvider _promptProvider;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<McpController> _logger;

    /// <summary>Initialises the controller with its dependencies.</summary>
    public McpController(
        IMcpToolProvider toolProvider,
        IMcpResourceProvider resourceProvider,
        IMcpPromptProvider promptProvider,
        IHostEnvironment environment,
        ILogger<McpController> logger)
    {
        _toolProvider = toolProvider;
        _resourceProvider = resourceProvider;
        _promptProvider = promptProvider;
        _environment = environment;
        _logger = logger;
    }

    /// <summary>Returns all registered MCP tools with their schemas.</summary>
    [HttpGet("tools")]
    public async Task<IActionResult> GetTools(CancellationToken ct)
    {
        var allTools = await _toolProvider.GetAllToolsAsync(ct);
        var dtos = FlattenTools(allTools)
            .Select(fn => new McpToolDto
            {
                Name = fn.Name,
                Description = fn.Description,
                InputSchema = fn.JsonSchema,
            })
            .ToList();

        _logger.LogInformation(
            "MCP tools listed. Count={Count} Names={Names}",
            dtos.Count,
            string.Join(",", dtos.Select(d => d.Name)));

        return Ok(dtos);
    }

    /// <summary>Returns all registered MCP resources.</summary>
    [HttpGet("resources")]
    public async Task<IActionResult> GetResources(CancellationToken ct)
    {
        var context = McpRequestContext.FromPrincipal(User);
        var resources = await _resourceProvider.ListAsync(string.Empty, context, ct);
        var dtos = resources
            .Select(r => new McpResourceDto
            {
                Uri = r.Uri,
                Name = r.Name,
                Description = r.Description ?? string.Empty,
                MimeType = r.MimeType,
            })
            .ToList();

        _logger.LogInformation(
            "MCP resources listed. Count={Count} Uris={Uris}",
            dtos.Count,
            string.Join(",", dtos.Select(d => d.Uri)));

        return Ok(dtos);
    }

    /// <summary>
    /// Returns all registered MCP prompts.
    /// Returns an empty array when no real <c>IMcpPromptProvider</c> is registered.
    /// </summary>
    [HttpGet("prompts")]
    public async Task<IActionResult> GetPrompts(CancellationToken ct)
    {
        var prompts = await _promptProvider.GetPromptsAsync(ct);
        var dtos = prompts
            .Select(p => new McpPromptDto
            {
                Name = p.Name,
                Description = p.Description,
                Arguments = p.Arguments,
            })
            .ToList();

        _logger.LogInformation(
            "MCP prompts listed. Count={Count} Names={Names}",
            dtos.Count,
            string.Join(",", dtos.Select(d => d.Name)));

        return Ok(dtos);
    }

    /// <summary>
    /// Invokes the named MCP tool with the supplied arguments.
    /// Emits a structured audit log entry (UserId, ToolName, InputHash) at Information level.
    /// Raw arguments are only logged at Debug level.
    /// </summary>
    [HttpPost("tools/{name}/invoke")]
    [RequestSizeLimit(32 * 1024)]
    public async Task<IActionResult> InvokeTool(string name, [FromBody] McpToolInvokeRequest request, CancellationToken ct)
    {
        // Enforce 32 KB body size limit manually so the check works in TestServer
        // (which does not implement IHttpMaxRequestBodySizeFeature used by [RequestSizeLimit]).
        const int maxBodyBytes = 32 * 1024;
        if (Request.ContentLength > maxBodyBytes)
            return StatusCode(StatusCodes.Status413RequestEntityTooLarge);

        if (request.Arguments.ValueKind == JsonValueKind.Undefined)
            return BadRequest("Arguments must be a valid JSON object.");

        var tool = await _toolProvider.GetToolByNameAsync(name, ct);
        if (tool is null)
            return NotFound();

        // Audit attribution goes through the single identity authority. Hand-rolling NameIdentifier here
        // logged an Entra caller carrying only an oid as "anonymous" — an audit trail that misattributes
        // a real caller is worse than one that admits it does not know.
        var userId = User.GetUserIdOrNull() ?? "anonymous";
        var rawArgs = request.Arguments.GetRawText();
        var inputHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawArgs))).ToLowerInvariant();

        // W3C trace id ties this audit entry to the rest of the request's spans across
        // systems — the "who authorized that call?" thread an auditor follows.
        var correlationId = Activity.Current?.TraceId.ToString() ?? "none";

        _logger.LogInformation(
            "MCP tool invoked. UserId={UserId} ToolName={ToolName} InputHash={InputHash} CorrelationId={CorrelationId}",
            userId, name, inputHash, correlationId);

        _logger.LogDebug(
            "MCP tool raw arguments. ToolName={ToolName} Arguments={Arguments}",
            name, request.Arguments);

        var args = new AIFunctionArguments();
        if (request.Arguments.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in request.Arguments.EnumerateObject())
                args[prop.Name] = prop.Value;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await tool.InvokeAsync(args, ct);
            sw.Stop();

            var output = result is JsonElement je
                ? je
                : JsonSerializer.SerializeToElement(result);

            _logger.LogInformation(
                "MCP tool completed. UserId={UserId} ToolName={ToolName} Status=success DurationMs={DurationMs} CorrelationId={CorrelationId}",
                userId, name, sw.ElapsedMilliseconds, correlationId);

            return Ok(new McpToolInvokeResponse
            {
                Output = output,
                DurationMs = sw.ElapsedMilliseconds,
                Success = true,
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex,
                "MCP tool completed. UserId={UserId} ToolName={ToolName} Status=error DurationMs={DurationMs} CorrelationId={CorrelationId}",
                userId, name, sw.ElapsedMilliseconds, correlationId);
            return Ok(new McpToolInvokeResponse
            {
                DurationMs = sw.ElapsedMilliseconds,
                Success = false,
                Error = _environment.IsDevelopment() ? ex.Message : "Tool execution failed. Check server logs.",
            });
        }
    }

    private static IEnumerable<AIFunction> FlattenTools(Dictionary<string, IList<AITool>> allTools)
        => allTools.Values.SelectMany(tools => tools).OfType<AIFunction>();
}
