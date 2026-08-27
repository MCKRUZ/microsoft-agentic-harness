using System.Diagnostics;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Escalation;
using Domain.AI.Models;
using Domain.Common.Config.AI.DirectToolInvocation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// Default <see cref="IDirectToolInvoker"/>: the one place a tool is armed, authorized, executed and
/// sanitized on behalf of an HTTP caller.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The invariant: identity and envelope must both be established before
/// <c>AdmitAsync</c>.</strong> <c>ToolInvocationGovernor</c>, the chain's first stage, reads the
/// ambient envelope to decide whether enforcement is active at all, and reads the execution context's
/// agent id as the subject to resolve permissions against — it denies outright when an envelope is
/// armed and that subject is missing. Both reads happen when the stage <em>runs</em>, not when either
/// value is published, so what matters is that neither is skipped and that both precede the admission
/// call. A refactor that moved either one past <c>AdmitAsync</c> — into the tool-resolution branch,
/// say — would produce a surface that denies every invocation, or one the governor cannot attribute.
/// </para>
/// <para>
/// <strong>The synthetic agent identity is not decoration.</strong> It is the subject permission rules
/// resolve against and the subject the governance audit records, so it must be stable per caller and
/// must not collide with a real agent's id — otherwise one caller's tool use is attributed to another,
/// or worse, inherits permission rules written for a named agent.
/// </para>
/// <para>
/// <strong>The deadline covers the whole admission chain, not just the tool.</strong> Authorization
/// can consult a policy engine, the classification gate can call a model, and a consumer's rule can
/// escalate to a human — so a deadline scoped to the tool call alone would leave total request time
/// unbounded by configuration, and a caller could be held indefinitely by an invocation that never
/// reached a tool at all.
/// </para>
/// <para>
/// <strong>No sandbox routing, deliberately.</strong> Direct invocation has agent-path parity: it runs
/// the tool in-process exactly as an agent turn does, so self-sandboxing tools still sandbox themselves
/// and capability flags are still enforced by the governor. Routing through <c>ISandboxExecutor</c>
/// would be the plan path's posture, and adopting it here would mean a caller's direct invocation and
/// the same tool called from their own workflow behaved differently.
/// </para>
/// <para>
/// <strong>No tool-output compression, deliberately.</strong> Compression exists to fit results into a
/// model's context window and does so by summarising and substituting pointers the agent can expand.
/// An HTTP caller cannot expand them, so compression would hand them an unusable reference in place of
/// their answer. Output is bounded by truncation instead, and truncation is reported.
/// </para>
/// </remarks>
public sealed partial class DirectToolInvoker : IDirectToolInvoker
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IToolCatalog _catalog;
    private readonly IOptionsMonitor<DirectToolInvocationConfig> _config;
    private readonly IMcpToolProvider? _mcpToolProvider;
    private readonly ILogger<DirectToolInvoker> _logger;

    /// <summary>Initializes the invoker.</summary>
    /// <param name="scopeFactory">Creates the per-invocation DI scope holding the scoped governor and execution context.</param>
    /// <param name="catalog">Resolves the tool, filtered to the caller's grant.</param>
    /// <param name="config">The host's direct-invocation settings, read per request.</param>
    /// <param name="mcpToolProvider">
    /// Resolves MCP-published tools for <see cref="InvokeMcpToolAsync"/> (#481). Optional, mirroring
    /// <c>ToolChainBuilder</c>'s own MCP dependency: a host with no MCP servers configured need not
    /// register a provider, and <see cref="InvokeMcpToolAsync"/> answers <see cref="DirectToolInvocationStatus.NotFound"/>
    /// rather than throwing when it is absent.
    /// </param>
    /// <param name="logger">Records denials, faults, and the governance trace.</param>
    public DirectToolInvoker(
        IServiceScopeFactory scopeFactory,
        IToolCatalog catalog,
        IOptionsMonitor<DirectToolInvocationConfig> config,
        ILogger<DirectToolInvoker> logger,
        IMcpToolProvider? mcpToolProvider = null)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _catalog = catalog;
        _config = config;
        _mcpToolProvider = mcpToolProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DirectToolInvocationOutcome> InvokeAsync(
        DirectToolInvocationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var config = _config.CurrentValue;
        if (!config.Enabled)
        {
            return DirectToolInvocationOutcome.Refused(
                DirectToolInvocationStatus.Disabled, "Direct tool invocation is not enabled on this host.");
        }

        var preflight = RunPreflight(request, config);
        if (preflight.Refusal is { } refusal)
            return refusal;

        // Unpacked here rather than threaded onward as a nullable pair: the accepted branch has both
        // values by construction, and passing them explicitly keeps the null-suppression to this one
        // line instead of scattering it through the arming path.
        return await RunArmedAsync(
            request, preflight.ToolName!, preflight.AgentId!, config, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Arms the invocation and runs it. Every exit path tears down what it armed.
    /// </summary>
    private Task<DirectToolInvocationOutcome> RunArmedAsync(
        DirectToolInvocationRequest request,
        string toolName,
        string agentId,
        DirectToolInvocationConfig config,
        CancellationToken cancellationToken)
    {
        var effectiveTimeout = request.RequestedTimeout ?? config.InvocationTimeout;
        return RunArmedCoreAsync(
            new ArmingRequest(toolName, agentId, request.Envelope, effectiveTimeout, TimeoutLogTemplate, FaultLogTemplate),
            cancellationToken,
            (admissionPipeline, scope, sw) =>
            {
                var armed = new ArmedInvocation(request, toolName, admissionPipeline, scope, config);
                return AuthorizeAndRunAsync(armed, effectiveTimeout, sw, cancellationToken);
            });
    }

    /// <summary>
    /// Runs the admission chain and then the tool itself, all under one deadline, and shapes the
    /// result for a caller outside the process.
    /// </summary>
    /// <remarks>
    /// The chain is called rather than reproduced. This path once ran its own copy of the gate
    /// sequence, which is how it came to run three of the four gates the agent path ran, in an order
    /// maintained by hand in two places at once.
    /// <para>
    /// The loop guard does not apply here and the request says so: it detects an agent repeating
    /// identical calls across a turn, and a single invocation has no sequence to evaluate.
    /// </para>
    /// </remarks>
    private async Task<DirectToolInvocationOutcome> AuthorizeAndRunAsync(
        ArmedInvocation armed, TimeSpan effectiveTimeout, Stopwatch sw, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(effectiveTimeout);

        // Parameters are passed for the same reason the agent path passes them: if a verdict is routed
        // to a human, approving a bare tool name tells them nothing. This is the surface most exposed
        // to external callers, so it is the one where an approver most needs to see what they are
        // signing off on.
        var admission = await armed.AdmissionPipeline
            .AdmitAsync(
                new ToolCallAdmissionRequest(
                    armed.ToolName, armed.Request.Parameters, CountsTowardLoopDetection: false),
                deadline.Token)
            .ConfigureAwait(false);

        if (!admission.IsAllowed)
        {
            // The chain's message is already scrubbed for consumption outside the host — rule ids,
            // paths and policy internals stay in the trace and the structured log.
            return Refused(DirectToolInvocationStatus.Denied, admission.DeniedMessage!, sw);
        }

        var tool = armed.Scope.ServiceProvider.GetKeyedService<ITool>(armed.ToolName);
        if (tool is null)
        {
            // The catalog described it, so it resolved once. Losing it here means the host cannot
            // construct it in this scope — answered as absence, matching how the catalog omits a tool
            // it cannot build.
            _logger.LogWarning("Tool {ToolName} is cataloged but did not resolve for invocation", armed.ToolName);
            return Refused(DirectToolInvocationStatus.NotFound, DirectToolInvocationErrors.NoSuchTool, sw);
        }

        ToolResult result;
        try
        {
            result = await tool
                .ExecuteAsync(armed.Request.Operation, armed.Request.Parameters, deadline.Token)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Reported before rethrowing so the deadline/cancellation/fault handling in
            // RunArmedAsync is unchanged — this only adds the approval-loop close, never
            // replaces the caller-facing outcome.
            await ApprovalExecutionReporting
                .ReportCallDidNotCompleteAsync(armed.AdmissionPipeline, admission, ReportedBy)
                .ConfigureAwait(false);
            throw;
        }

        // #460: the raw failure text is passed through untreated — ToolCallAdmissionPipeline
        // sanitizes, redacts, and bounds it exactly once, at the chokepoint every reporting path
        // (this one and GovernedAIFunction's) funnels through, rather than each duplicating that
        // treatment.
        await ReportExecutionAsync(
            armed.AdmissionPipeline,
            admission,
            result.Success
                ? new ToolExecutionReport(EscalationExecutionStatus.Succeeded, null, null)
                : new ToolExecutionReport(
                    EscalationExecutionStatus.Failed,
                    result.Error ?? "the tool reported failure with no message",
                    null,
                    ToolName: armed.ToolName),
            ReportedBy)
            .ConfigureAwait(false);

        sw.Stop();
        return Shape(result, armed, admission, sw.Elapsed);
    }

    private const string ReportedBy = "direct-invocation";

    /// <summary>
    /// Closes the approval loop for this call, when it was one a human approved.
    /// </summary>
    /// <param name="reportedBy">
    /// The calling surface's own identifier (#494) — the MCP path used to inline this same call
    /// rather than share this helper, specifically because the helper used to hard-wire
    /// <see cref="ReportedBy"/>; now both surfaces resolve to the identical call, differing only in
    /// which constant they pass (<see cref="ReportedBy"/> or <c>McpReportedBy</c>).
    /// </param>
    /// <remarks>
    /// Deliberately reported on <see cref="CancellationToken.None"/>, not the deadline or caller
    /// token — both are typically already fired by the time this runs (a fault means the deadline
    /// elapsed, or the caller went away), and a report cancelled by the same token that caused the
    /// failure would silently drop exactly the report that failure needs to reach the approver.
    /// <see cref="IToolCallAdmissionPipeline.ReportExecutionAsync"/> never throws — see its own
    /// must-not-throw contract — so this is not itself a new fault surface.
    /// </remarks>
    private static ValueTask ReportExecutionAsync(
        IToolCallAdmissionPipeline pipeline, ToolCallAdmission admission, ToolExecutionReport report, string reportedBy) =>
        pipeline.ReportExecutionAsync(admission, report, reportedBy, CancellationToken.None);

    /// <summary>
    /// Everything an armed invocation needs, gathered once rather than threaded through each step as a
    /// widening parameter list.
    /// </summary>
    /// <param name="Request">The caller's request.</param>
    /// <param name="ToolName">The catalog's name for the tool, which is also its keyed-DI key.</param>
    /// <param name="AdmissionPipeline">This invocation's scoped admission chain.</param>
    /// <param name="Scope">The invocation's DI scope, from which the tool itself is resolved.</param>
    /// <param name="Config">The host settings this invocation was admitted under.</param>
    private readonly record struct ArmedInvocation(
        DirectToolInvocationRequest Request,
        string ToolName,
        IToolCallAdmissionPipeline AdmissionPipeline,
        AsyncServiceScope Scope,
        DirectToolInvocationConfig Config);
}
