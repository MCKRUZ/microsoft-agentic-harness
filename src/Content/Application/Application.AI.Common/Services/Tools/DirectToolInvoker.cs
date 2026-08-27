using System.Diagnostics;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Governance;
using Domain.AI.Escalation;
using Domain.AI.Governance;
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
        CancellationToken cancellationToken) =>
        RunArmedCoreAsync(
            toolName, agentId, request.Envelope,
            request.RequestedTimeout ?? config.InvocationTimeout,
            TimeoutLogTemplate, FaultLogTemplate, cancellationToken,
            (admissionPipeline, scope, sw) =>
            {
                var armed = new ArmedInvocation(request, toolName, admissionPipeline, scope, config);
                return AuthorizeAndRunAsync(armed, sw, cancellationToken);
            });

    /// <summary>
    /// Kept as full message templates, not assembled from a shared prefix + suffix, so the rendered
    /// text AND the structured log's message-template string (what Application Insights groups and
    /// alerts by) are both byte-identical to what this surface has always logged — found in review on
    /// #494: an earlier version of this refactor collapsed both surfaces onto one shared template with
    /// the surface name demoted to a property, which keeps the rendered text but silently breaks any
    /// saved query or alert keyed on the old, now-vanished template string.
    /// </summary>
    private const string TimeoutLogTemplate = "Direct invocation of {ToolName} exceeded its {Timeout} deadline";

    /// <summary>See <see cref="TimeoutLogTemplate"/>'s remarks — the fault-branch counterpart.</summary>
    private const string FaultLogTemplate = "Direct invocation of {ToolName} threw";

    /// <summary>
    /// The arm/authorize/catch skeleton every direct-invocation surface runs, whatever kind of tool
    /// it turns out to be (#481) — extracted so <see cref="RunArmedAsync"/> and
    /// <c>DirectToolInvoker.Mcp.cs</c>'s <c>RunMcpArmedAsync</c> share the one implementation rather
    /// than each re-deriving it by hand (#494). <see cref="ArmGovernance"/> was already extracted for
    /// exactly this reason but stopped one level too shallow — the scope creation, the arm, the
    /// three-arm catch ladder, and the trace log around all of it were still duplicated.
    /// </summary>
    /// <param name="toolName">The tool being invoked, for the trace log and the deadline/fault log messages.</param>
    /// <param name="agentId">The caller's synthetic identity — see <see cref="ArmGovernance"/>'s remarks.</param>
    /// <param name="envelope">The caller's capability envelope to arm around the call.</param>
    /// <param name="effectiveTimeout">
    /// The deadline this invocation runs under, already resolved from the caller's request and the
    /// host's config — reported in the timeout log message, not enforced here; enforcement is
    /// <paramref name="body"/>'s own concern, since only it knows where the linked deadline token
    /// needs to reach.
    /// </param>
    /// <param name="timeoutLogTemplate">
    /// The caller's own full message template for the deadline-timeout warning, e.g.
    /// <see cref="TimeoutLogTemplate"/> — a complete template, not a prefix this method assembles
    /// into a shared one, so the structured log's message-template string (what a log backend groups
    /// and alerts by) stays exactly what each surface has always emitted.
    /// </param>
    /// <param name="faultLogTemplate">The caller's own full message template for the fault-branch error, e.g. <see cref="FaultLogTemplate"/>.</param>
    /// <param name="cancellationToken">The caller's own token — see the first catch arm for why it
    /// alone distinguishes "the caller went away" from "the deadline elapsed".</param>
    /// <param name="body">
    /// Authorizes and runs the tool itself, given the armed pipeline, the invocation's DI scope (used
    /// by the keyed-DI path to resolve the tool; ignored by the MCP path, which already holds its
    /// <c>AIFunction</c>), and the shared stopwatch every outcome's <c>Duration</c> is measured against.
    /// </param>
    private async Task<DirectToolInvocationOutcome> RunArmedCoreAsync(
        string toolName,
        string agentId,
        Domain.AI.Bundles.CapabilityEnvelope envelope,
        TimeSpan effectiveTimeout,
        string timeoutLogTemplate,
        string faultLogTemplate,
        CancellationToken cancellationToken,
        Func<IToolCallAdmissionPipeline, AsyncServiceScope, Stopwatch, Task<DirectToolInvocationOutcome>> body)
    {
        var sw = Stopwatch.StartNew();

        await using var scope = _scopeFactory.CreateAsyncScope();

        try
        {
            // Identity and envelope. Both must be in place before AuthorizeAsync — see the type
            // remarks for what the governor reads, and when. The conversation-id and
            // callOnceScopeId choices ArmGovernance makes are documented on ArmGovernance itself,
            // not repeated here — this method only calls it (#494: the repeat was stale
            // documentation left behind when ArmGovernance was first extracted).
            var (admissionPipeline, grantedEnvelope, armedAdmission) =
                ArmGovernance(scope.ServiceProvider, agentId, envelope);
            using var _grantedEnvelope = grantedEnvelope;
            using var _armedAdmission = armedAdmission;

            try
            {
                return await body(admissionPipeline, scope, sw).ConfigureAwait(false);
            }
            finally
            {
                LogTrace(toolName, agentId, admissionPipeline.GetTrace);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller went away or the host is shutting down. Distinct from the deadline below:
            // nobody is waiting for an answer, so unwind rather than manufacture one.
            throw;
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            _logger.LogWarning(timeoutLogTemplate, toolName, effectiveTimeout);
            return DirectToolInvocationOutcome.Refused(
                DirectToolInvocationStatus.TimedOut, "The tool did not complete within its deadline.", sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            // Full detail stays in the structured log. Tool and infrastructure exceptions carry host
            // paths, connection strings and container internals, and this response crosses a trust
            // boundary — so the caller gets a stable code and nothing derived from the exception.
            _logger.LogError(ex, faultLogTemplate, toolName);
            return DirectToolInvocationOutcome.Refused(
                DirectToolInvocationStatus.Faulted, DirectToolInvocationErrors.Failed, sw.Elapsed);
        }
    }

    /// <summary>
    /// Establishes the caller's identity and capability envelope, then arms the ambient admission
    /// chain — the fixed-order sequence every direct-invocation surface must run before its own
    /// tool-specific resolution, whatever kind of tool that turns out to be (#481). Extracted so the
    /// keyed-DI <see cref="ITool"/> path (<see cref="RunArmedAsync"/>) and the MCP path
    /// (<c>DirectToolInvoker.Mcp.cs</c>) share the one implementation rather than each re-deriving this
    /// order by hand — see this type's remarks for what goes wrong when that discipline is duplicated.
    /// </summary>
    /// <returns>
    /// The scoped admission chain, reset and ready, plus the two restoring scopes the caller must
    /// dispose (in either order — both restore independently) when the invocation ends.
    /// </returns>
    private (IToolCallAdmissionPipeline Pipeline, IDisposable EnvelopeScope, IDisposable AdmissionScope) ArmGovernance(
        IServiceProvider scopedProvider, string agentId, Domain.AI.Bundles.CapabilityEnvelope envelope)
    {
        // Identity and envelope. Both must be in place before AdmitAsync — see the type remarks for
        // what the governor reads, and when.
        //
        // The conversation id is deliberately NOT agentId. #325's retry-attribution memory is keyed on
        // (conversation, agent, tool) precisely so it expires — but agentId here is the caller's stable
        // synthetic identity, reused for every direct invocation that caller ever makes. Passing it as
        // the conversation id too would give the failure-memory key no expiry at all. A direct
        // invocation is a single, standalone call with no request-level session concept to key on, so
        // each one mints its own one-shot id.
        //
        // callOnceScopeId is deliberately omitted (null), for the identical reason: a direct invocation
        // has no request-level session to key a repeat-call check on either. A call-once tool reached
        // through this surface is therefore not enforceable here — the call-once gate fails open on a
        // null scope, which is the documented, correct answer, not a gap. See
        // IAgentExecutionContext.CallOnceScopeId's remarks.
        var executionContext = scopedProvider.GetRequiredService<IAgentExecutionContext>();
        executionContext.Initialize(agentId, conversationId: Guid.NewGuid().ToString(), turnNumber: 1);

        // Required, not GetService: the chain is registered unconditionally, and an absent one is
        // indistinguishable at runtime from a host whose gates all happen to be off — so tolerating
        // null here would let a broken composition run this path silently unguarded.
        var admissionPipeline = scopedProvider.GetRequiredService<IToolCallAdmissionPipeline>();
        admissionPipeline.Reset();

        // Both ambient values are published with restoring scopes rather than assigned and nulled. The
        // difference only shows under nesting, where it is the whole game: nulling on teardown disarms
        // whatever an enclosing flow had armed, leaving the outer call ungoverned for the rest of its
        // life. Restoring cannot do that.
        //
        // The chain is armed as well as called directly, because a tool that spawns an agent turn
        // beneath it must reach the same chain rather than run unadmitted.
        var grantedEnvelope = CapabilityEnvelopeAccessor.Begin(envelope);
        var armedAdmission = ToolAdmissionAccessor.Begin(admissionPipeline);

        return (admissionPipeline, grantedEnvelope, armedAdmission);
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
        ArmedInvocation armed, Stopwatch sw, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(armed.Request.RequestedTimeout ?? armed.Config.InvocationTimeout);

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
    /// Builds a refusal and stops the clock. This method owns stopping it, so callers must not — a
    /// second stop is harmless but splits ownership, and a caller that forgets is then
    /// indistinguishable from one that did not need to.
    /// </summary>
    private static DirectToolInvocationOutcome Refused(
        DirectToolInvocationStatus status, string error, Stopwatch sw)
    {
        sw.Stop();
        return DirectToolInvocationOutcome.Refused(status, error, sw.Elapsed);
    }

    /// <summary>
    /// Records what governance decided. The trace carries rule ids and policy reasons, so it is written
    /// to the host's log and never returned to the caller.
    /// </summary>
    private void LogTrace(string toolName, string agentId, Func<GovernanceTrace> trace)
    {
        // Checked before calling the factory: GetTrace() snapshots the decision list under a lock, and
        // a production host running at Warning would pay for that on every invocation to log nothing.
        if (!_logger.IsEnabled(LogLevel.Information))
            return;

        foreach (var decision in trace().ToolDecisions)
        {
            _logger.LogInformation(
                "Direct invocation governance: caller {AgentId} tool {ToolName} → {Outcome} ({Reason}); enforced={Enforced}",
                agentId, toolName, decision.Outcome, decision.Reason, decision.Enforced);
        }
    }

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
