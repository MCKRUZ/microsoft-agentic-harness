using System.Diagnostics;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Governance;
using Domain.AI.Bundles;
using Domain.AI.Escalation;
using Domain.Common.Config.AI.DirectToolInvocation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// The MCP-tool half of direct invocation (#481): everything <see cref="DirectToolInvoker.InvokeAsync"/>
/// does for a keyed-DI <see cref="ITool"/>, run instead against a tool published by a host-connected MCP
/// server. Shares the identity/envelope/admission arming (<see cref="DirectToolInvoker.ArmGovernance"/>)
/// and the response-shaping primitives (<c>DirectToolInvoker.Response.cs</c>) with the keyed-DI path —
/// only tool resolution and invocation differ, which is the one place the two kinds of tool are
/// genuinely different.
/// </summary>
public sealed partial class DirectToolInvoker
{
    private const string McpReportedBy = "direct-invocation-mcp";

    /// <inheritdoc />
    public async Task<DirectToolInvocationOutcome> InvokeMcpToolAsync(
        DirectMcpToolInvocationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var config = _config.CurrentValue;
        if (!config.McpEnabled)
        {
            return DirectToolInvocationOutcome.Refused(
                DirectToolInvocationStatus.Disabled, "MCP tool invocation is not enabled on this host.");
        }

        var preflight = await RunMcpPreflightAsync(request, config, cancellationToken).ConfigureAwait(false);
        if (preflight.Refusal is { } refusal)
            return refusal;

        return await RunMcpArmedAsync(
            request, preflight.Tool!, preflight.AgentId!, config, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Validates the request, resolves the tool against the caller's grant, and mints the identity the
    /// invocation will run under — the MCP analogue of <c>DirectToolInvoker.Admission.cs</c>'s
    /// <c>RunPreflight</c>, async because resolving an MCP tool means contacting a server.
    /// </summary>
    private async Task<McpPreflight> RunMcpPreflightAsync(
        DirectMcpToolInvocationRequest request, DirectToolInvocationConfig config, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ToolName))
            return McpPreflight.Refuse(DirectToolInvocationStatus.Invalid, "A tool name is required.");

        if (request.Arguments.Count > config.MaxParameterCount)
        {
            return McpPreflight.Refuse(
                DirectToolInvocationStatus.Invalid,
                $"An invocation may pass at most {config.MaxParameterCount} arguments.");
        }

        if (request.RequestedTimeout is { } requested
            && !(requested > TimeSpan.Zero && requested <= config.InvocationTimeout))
        {
            return McpPreflight.Refuse(
                DirectToolInvocationStatus.Invalid,
                $"Requested timeout must be positive and no greater than {config.InvocationTimeout}.");
        }

        var agentId = SyntheticAgentPrefix + request.OwnerId;
        if (!PlanRunRequest.IsWellFormedAgentId(agentId))
        {
            _logger.LogWarning(
                "Direct MCP invocation rejected: caller identity is unusable as a permission subject (length {Length})",
                request.OwnerId?.Length ?? 0);
            return McpPreflight.Refuse(
                DirectToolInvocationStatus.IdentityUnusable,
                "The authenticated principal carries no usable identity.");
        }

        // AllowedTools gates the specific tool name on top of AllowedMcpServers (see
        // CapabilityEnvelope's remarks: a granted server does not imply every tool it publishes is
        // granted) — checked before any server is contacted, so an ungranted tool costs a list lookup,
        // not an outbound call.
        if (!request.Envelope.GrantsTool(request.ToolName))
            return McpPreflight.Refuse(DirectToolInvocationStatus.NotFound, DirectToolInvocationErrors.NoSuchTool);

        if (_mcpToolProvider is null)
            return McpPreflight.Refuse(DirectToolInvocationStatus.NotFound, DirectToolInvocationErrors.NoSuchTool);

        var tool = await ResolveGrantedMcpToolAsync(request.ToolName, request.Envelope, cancellationToken)
            .ConfigureAwait(false);
        if (tool is null)
            return McpPreflight.Refuse(DirectToolInvocationStatus.NotFound, DirectToolInvocationErrors.NoSuchTool);

        return McpPreflight.Accept(tool, agentId);
    }

    /// <summary>
    /// Resolves <paramref name="toolName"/> by contacting only the servers <paramref name="envelope"/>
    /// grants, mirroring <c>ToolChainBuilder.ResolveInjectedMcpToolsAsync</c>'s SSRF-safe pattern — an
    /// ungranted server is never contacted at all, no side-effect connection and no schema disclosure.
    /// Unlike <see cref="IMcpToolProvider.GetToolByNameAsync"/>, which searches every configured server
    /// regardless of grant, this never widens the search beyond <see cref="CapabilityEnvelope.AllowedMcpServers"/>.
    /// </summary>
    private async Task<AIFunction?> ResolveGrantedMcpToolAsync(
        string toolName, CapabilityEnvelope envelope, CancellationToken cancellationToken)
    {
        foreach (var server in envelope.AllowedMcpServers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IList<AITool> tools;
            try
            {
                tools = await _mcpToolProvider!.GetToolsAsync(server, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A granted server that cannot be reached costs this one caller a NotFound, not the
                // whole invocation — the same "skip, don't fail the build" posture ToolChainBuilder
                // takes for the identical failure.
                _logger.LogWarning(ex,
                    "Direct MCP invocation: granted server {Server} could not be reached while resolving {ToolName}",
                    server, toolName);
                continue;
            }

            var match = tools.OfType<AIFunction>()
                .FirstOrDefault(t => string.Equals(t.Name, toolName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }

        return null;
    }

    /// <summary>
    /// The full message templates this surface has always logged — kept distinct from the keyed-DI
    /// path's own <c>TimeoutLogTemplate</c>/<c>FaultLogTemplate</c> rather than assembled from a
    /// shared prefix, so a log backend grouping by message template still sees two surfaces, not one
    /// with a property tacked on. See <c>DirectToolInvoker.TimeoutLogTemplate</c>'s remarks.
    /// </summary>
    private const string McpTimeoutLogTemplate = "Direct MCP invocation of {ToolName} exceeded its {Timeout} deadline";

    /// <summary>See <see cref="McpTimeoutLogTemplate"/>'s remarks — the fault-branch counterpart.</summary>
    private const string McpFaultLogTemplate = "Direct MCP invocation of {ToolName} threw";

    /// <summary>Arms the invocation and runs it, sharing <see cref="DirectToolInvoker.RunArmedCoreAsync"/>
    /// with the keyed-DI path (#494) rather than mirroring it by hand.</summary>
    private Task<DirectToolInvocationOutcome> RunMcpArmedAsync(
        DirectMcpToolInvocationRequest request,
        AIFunction tool,
        string agentId,
        DirectToolInvocationConfig config,
        CancellationToken cancellationToken)
    {
        var effectiveTimeout = request.RequestedTimeout ?? config.InvocationTimeout;
        return RunArmedCoreAsync(
            new ArmingRequest(request.ToolName, agentId, request.Envelope, effectiveTimeout, McpTimeoutLogTemplate, McpFaultLogTemplate),
            cancellationToken,
            (admissionPipeline, _, sw) =>
                AuthorizeAndRunMcpAsync(request, tool, admissionPipeline, config, effectiveTimeout, sw, cancellationToken));
    }

    /// <summary>
    /// Runs the admission chain and then the MCP tool itself, mirroring
    /// <see cref="DirectToolInvoker.AuthorizeAndRunAsync"/> exactly — same gate call, same reporting
    /// call, same fail-closed output policy — the only difference is how the tool is invoked and how
    /// its result is told apart from a failure.
    /// </summary>
    private async Task<DirectToolInvocationOutcome> AuthorizeAndRunMcpAsync(
        DirectMcpToolInvocationRequest request,
        AIFunction tool,
        IToolCallAdmissionPipeline admissionPipeline,
        DirectToolInvocationConfig config,
        TimeSpan effectiveTimeout,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(effectiveTimeout);

        var admission = await admissionPipeline
            .AdmitAsync(
                new ToolCallAdmissionRequest(request.ToolName, request.Arguments, CountsTowardLoopDetection: false),
                deadline.Token)
            .ConfigureAwait(false);

        if (!admission.IsAllowed)
            return Refused(DirectToolInvocationStatus.Denied, admission.DeniedMessage!, sw);

        // Same failure normalization production tool chains apply to every MCP-sourced function before
        // GovernedAIFunction ever sees the result — an MCP tool's own non-throwing failure shape is
        // normalized to the one marker both this method and GovernedAIFunction recognize.
        var normalized = new McpFailureNormalizingAIFunction(tool);
        var arguments = new AIFunctionArguments(new Dictionary<string, object?>(request.Arguments));

        object? rawResult;
        try
        {
            rawResult = await normalized.InvokeAsync(arguments, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await ApprovalExecutionReporting
                .ReportCallDidNotCompleteAsync(admissionPipeline, admission, McpReportedBy)
                .ConfigureAwait(false);
            throw;
        }

        var failure = rawResult as ConvertedToolFailure;
        var failureText = failure?.ErrorText;

        // #460: the raw failure text is passed through untreated — ShapeMcp sanitizes, redacts, and
        // bounds it exactly once, at the same chokepoint the keyed-DI path's Shape uses.
        await ReportExecutionAsync(
            admissionPipeline,
            admission,
            failureText is null
                ? new ToolExecutionReport(EscalationExecutionStatus.Succeeded, null, null)
                : new ToolExecutionReport(EscalationExecutionStatus.Failed, failureText, null, ToolName: request.ToolName),
            McpReportedBy).ConfigureAwait(false);

        sw.Stop();
        return await ShapeMcpAsync(
            failureText, rawResult, request.ToolName, admissionPipeline, admission, config, sw.Elapsed,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reduces an MCP result to <see cref="ShapeTextAsync"/>'s shared shape — the MCP analogue of
    /// <c>DirectToolInvoker.Response.cs</c>'s <c>ShapeAsync</c>, differing only in how the raw text is
    /// obtained: a keyed-DI <c>ToolResult</c> already carries a string, an MCP result needs
    /// <see cref="ToolResultText.ExtractText"/> first.
    /// </summary>
    private Task<DirectToolInvocationOutcome> ShapeMcpAsync(
        string? failureText,
        object? rawResult,
        string toolName,
        IToolCallAdmissionPipeline admissionPipeline,
        ToolCallAdmission admission,
        DirectToolInvocationConfig config,
        TimeSpan duration,
        CancellationToken cancellationToken) =>
        ShapeTextAsync(
            failureText,
            // ShapeTextAsync never reads successText on the failure branch, and ExtractText's own
            // default case does a full JsonSerializer.Serialize of the raw result — not worth paying on
            // every MCP failure just to build a value that gets thrown away.
            failureText is null ? ToolResultText.ExtractText(rawResult) : string.Empty,
            toolName,
            admissionPipeline,
            admission,
            config.MaxOutputCharacters,
            duration,
            _logger,
            cancellationToken);

    /// <summary>
    /// The result of MCP pre-flight: either a refusal to return as-is, or the resolved tool and caller
    /// identity to run with. Kept separate from <c>DirectToolInvoker.Admission.cs</c>'s <c>Preflight</c>
    /// rather than widening that type — the keyed-DI path resolves a name, this one resolves and keeps
    /// the <see cref="AIFunction"/> itself, since re-resolving it in the run stage would mean a second
    /// round trip to the server pre-flight already contacted.
    /// </summary>
    private readonly record struct McpPreflight(
        DirectToolInvocationOutcome? Refusal, AIFunction? Tool, string? AgentId)
    {
        public static McpPreflight Refuse(DirectToolInvocationStatus status, string error) =>
            new(DirectToolInvocationOutcome.Refused(status, error), null, null);

        public static McpPreflight Accept(AIFunction tool, string agentId) => new(null, tool, agentId);
    }
}
