using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Tools;
using Domain.AI.Bundles;
using Domain.AI.Governance;
using Domain.AI.MCP;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
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
    private readonly ICapabilityEnvelopeResolver _envelopeResolver;
    private readonly IDirectToolInvoker _invoker;
    private readonly ILogger<McpController> _logger;

    /// <summary>Initialises the controller with its dependencies.</summary>
    public McpController(
        IMcpToolProvider toolProvider,
        IMcpResourceProvider resourceProvider,
        IMcpPromptProvider promptProvider,
        ICapabilityEnvelopeResolver envelopeResolver,
        IDirectToolInvoker invoker,
        ILogger<McpController> logger)
    {
        _toolProvider = toolProvider;
        _resourceProvider = resourceProvider;
        _promptProvider = promptProvider;
        _envelopeResolver = envelopeResolver;
        _invoker = invoker;
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
    /// Invokes the named MCP tool with the supplied arguments, under the same admission, sanitize, and
    /// output-bounding chokepoint every other direct-invocation surface in this host runs under (#481).
    /// Emits a structured audit log entry (UserId, ToolName, InputHash) at Information level.
    /// Raw arguments are only logged at Debug level.
    /// </summary>
    /// <remarks>
    /// <strong>This is not a listing endpoint's trust boundary.</strong> Discovery (<see cref="GetTools"/>)
    /// requires only authentication, because listing confers nothing. This action executes host- or
    /// server-side code on a caller's say-so, so it is gated three times: the caller must authenticate,
    /// must present a stable identifier the governance layer can attribute the call to, and the tool
    /// must be reachable through a server their capability envelope grants — enforced inside
    /// <see cref="IDirectToolInvoker.InvokeMcpToolAsync"/>, not here.
    /// </remarks>
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

        // Audit attribution goes through the single identity authority. Hand-rolling NameIdentifier here
        // logged an Entra caller carrying only an oid as "anonymous" — an audit trail that misattributes
        // a real caller is worse than one that admits it does not know. #481: this identifier doubles as
        // the governance permission subject now, so a caller with none is refused outright rather than
        // bucketed under a shared "anonymous" identity that would let one caller's denial or grant leak
        // onto another's.
        var userId = User.GetUserIdOrNull();
        if (string.IsNullOrEmpty(userId))
            return this.NoUsableIdentity();

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

        var arguments = new Dictionary<string, object?>();
        if (request.Arguments.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in request.Arguments.EnumerateObject())
                arguments[prop.Name] = prop.Value;
        }

        DirectToolInvocationOutcome outcome;
        using (McpToolListCacheAccessor.Begin())
        {
            // Scoped around envelope resolution AND invocation, not just the former: on the
            // ungranted-caller fallback (see ResolveMcpEnvelopeAsync's remarks), the envelope resolves
            // by fetching every connected server's tools, and the invoker's own grant re-resolution
            // (DirectToolInvoker.ResolveGrantedMcpToolAsync) re-fetches each of those same servers one
            // at a time to find the one being invoked — #495. Both calls must share one cache scope for
            // the second to ever hit it.
            outcome = await _invoker.InvokeMcpToolAsync(
                new DirectMcpToolInvocationRequest
                {
                    ToolName = name,
                    Arguments = arguments,
                    OwnerId = userId,
                    Envelope = await ResolveMcpEnvelopeAsync(ct)
                },
                ct);
        }

        _logger.LogInformation(
            "MCP tool completed. UserId={UserId} ToolName={ToolName} Status={Status} DurationMs={DurationMs} CorrelationId={CorrelationId}",
            userId, name, outcome.Status, outcome.Duration.TotalMilliseconds, correlationId);

        return ToActionResult(outcome);
    }

    /// <summary>
    /// Shapes a governed outcome into the HTTP response, mirroring <c>ToolsController.Invoke</c>'s
    /// status mapping: absent, ungranted, and not-offered-here collapse to one answer, and a tool that
    /// ran and said no is a different, successful-HTTP-status event from governance refusing to run it
    /// at all.
    /// </summary>
    /// <remarks>
    /// The envelope this resolves is what <see cref="ResolveMcpEnvelopeAsync"/> defaults to when no
    /// operator has configured a narrower one for this caller — see that method's remarks for why that
    /// default is deliberately open for MCP specifically.
    /// </remarks>
    private async Task<CapabilityEnvelope> ResolveMcpEnvelopeAsync(CancellationToken ct)
    {
        var resolved = _envelopeResolver.Resolve(User);

        // AllowedMcpServers OR'd with AllowedTools, deliberately. An earlier version of this check
        // narrowed to AllowedMcpServers alone, reasoning that AllowedTools is "shared with the
        // unrelated keyed-DI surface" — but CapabilityEnvelope.AllowedTools's own remarks say
        // otherwise: "Tools reached through a granted MCP server are no exception — AllowedMcpServers
        // controls which servers may be contacted, while invoking any tool it publishes still requires
        // that tool's name here." AllowedTools IS the MCP tool gate too (DirectToolInvoker.Mcp.cs
        // enforces it via Envelope.GrantsTool). Narrowing this check to AllowedMcpServers alone meant
        // an operator who deliberately configured a least-privilege grant naming only tools — with a
        // restrictive AutonomyCeiling and no MCP servers at all — had that grant silently discarded and
        // replaced with every connected server, every tool it publishes, and Autonomous ceiling: a
        // fail-open privilege escalation caught by independent security review, not the two prior
        // passes. Only a caller with NOTHING granted anywhere — the resolver's genuine fail-closed
        // default — should reach the auto-open fallback below.
        if (resolved.AllowedMcpServers.Count > 0 || resolved.AllowedTools.Count > 0)
            return resolved;

        // No operator-configured grant for this caller — the shared resolver's fail-closed default.
        // For every other direct-invocation surface that means "deny", by design (see
        // DirectToolInvocationConfig.Enabled's remarks). MCP is the deliberate exception
        // (DirectToolInvocationConfig.McpEnabled): a tool reached through the MCP panel is one an
        // operator already decided to trust by connecting this host to its server
        // (AppConfig.AI.McpServers), so the default here grants every currently-connected server and
        // the tools it publishes, rather than requiring a second, redundant grant on top of that
        // connection decision. AutonomyCeiling is Autonomous because a human clicking "invoke" in the
        // panel is the approval — there is no approval-routing UI on this surface to defer to instead,
        // and anything less than Autonomous fails every call closed (see CapabilityEnvelope.AutonomyCeiling).
        var allTools = await _toolProvider.GetAllToolsAsync(ct);
        return new CapabilityEnvelope
        {
            AllowedMcpServers = [.. allTools.Keys],
            AllowedTools = [.. allTools.Values.SelectMany(tools => tools).Select(tool => tool.Name)],
            AutonomyCeiling = AutonomyLevel.Autonomous
        };
    }

    private IActionResult ToActionResult(DirectToolInvocationOutcome outcome) => outcome.Status switch
    {
        DirectToolInvocationStatus.Succeeded or DirectToolInvocationStatus.ToolFailed => Ok(new McpToolInvokeResponse
        {
            Output = outcome.Output,
            OutputTruncated = outcome.OutputTruncated,
            DurationMs = (long)outcome.Duration.TotalMilliseconds,
            Success = outcome.Status == DirectToolInvocationStatus.Succeeded,
            Error = outcome.Error,
        }),

        DirectToolInvocationStatus.NotFound => NotFound(),

        DirectToolInvocationStatus.IdentityUnusable => this.NoUsableIdentity(),

        DirectToolInvocationStatus.Invalid => Problem(
            title: "Validation failed", detail: outcome.Error, statusCode: StatusCodes.Status400BadRequest),

        // Forbid() rather than a Problem body — governance-refused and the surface being switched off
        // are deliberately not told apart, matching ToolsController.Invoke.
        DirectToolInvocationStatus.Denied or DirectToolInvocationStatus.Disabled => Forbid(),

        DirectToolInvocationStatus.TimedOut => Problem(
            title: "Tool timed out", detail: outcome.Error, statusCode: StatusCodes.Status504GatewayTimeout),

        _ => Problem(
            title: "Tool invocation failed", detail: outcome.Error,
            statusCode: StatusCodes.Status500InternalServerError)
    };

    private static IEnumerable<AIFunction> FlattenTools(Dictionary<string, IList<AITool>> allTools)
        => allTools.Values.SelectMany(tools => tools).OfType<AIFunction>();
}
