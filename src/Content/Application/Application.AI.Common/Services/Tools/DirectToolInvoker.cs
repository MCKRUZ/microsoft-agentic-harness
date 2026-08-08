using System.Diagnostics;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Governance;
using Domain.AI.Governance;
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
    private readonly ICompositeResponseSanitizer _sanitizer;
    private readonly IOptionsMonitor<DirectToolInvocationConfig> _config;
    private readonly ILogger<DirectToolInvoker> _logger;

    /// <summary>Initializes the invoker.</summary>
    /// <param name="scopeFactory">Creates the per-invocation DI scope holding the scoped governor and execution context.</param>
    /// <param name="catalog">Resolves the tool, filtered to the caller's grant.</param>
    /// <param name="sanitizer">Scrubs everything leaving the trust boundary.</param>
    /// <param name="config">The host's direct-invocation settings, read per request.</param>
    /// <param name="logger">Records denials, faults, and the governance trace.</param>
    public DirectToolInvoker(
        IServiceScopeFactory scopeFactory,
        IToolCatalog catalog,
        ICompositeResponseSanitizer sanitizer,
        IOptionsMonitor<DirectToolInvocationConfig> config,
        ILogger<DirectToolInvoker> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _catalog = catalog;
        _sanitizer = sanitizer;
        _config = config;
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
    private async Task<DirectToolInvocationOutcome> RunArmedAsync(
        DirectToolInvocationRequest request,
        string toolName,
        string agentId,
        DirectToolInvocationConfig config,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        await using var scope = _scopeFactory.CreateAsyncScope();

        try
        {
            // Identity and envelope. Both must be in place before AuthorizeAsync — see the type
            // remarks for what the governor reads, and when.
            var executionContext = scope.ServiceProvider.GetRequiredService<IAgentExecutionContext>();
            executionContext.Initialize(agentId, agentId, turnNumber: 1);

            // Required, not GetService: the chain is registered unconditionally, and an absent one is
            // indistinguishable at runtime from a host whose gates all happen to be off — so tolerating
            // null here would let a broken composition run this path silently unguarded.
            var admissionPipeline = scope.ServiceProvider.GetRequiredService<IToolCallAdmissionPipeline>();
            admissionPipeline.Reset();

            // Both ambient values are published with restoring scopes rather than assigned and nulled.
            // The difference only shows under nesting, where it is the whole game: nulling on teardown
            // disarms whatever an enclosing flow had armed, leaving the outer call ungoverned for the
            // rest of its life. Restoring cannot do that.
            //
            // The chain is armed as well as called directly, because a tool that spawns an agent turn
            // beneath it must reach the same chain rather than run unadmitted.
            using var grantedEnvelope = CapabilityEnvelopeAccessor.Begin(request.Envelope);
            using var armedAdmission = ToolAdmissionAccessor.Begin(admissionPipeline);

            try
            {
                var armed = new ArmedInvocation(request, toolName, admissionPipeline, scope, config);
                return await AuthorizeAndRunAsync(armed, sw, cancellationToken).ConfigureAwait(false);
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
            _logger.LogWarning(
                "Direct invocation of {ToolName} exceeded its {Timeout} deadline",
                toolName, request.RequestedTimeout ?? config.InvocationTimeout);
            return DirectToolInvocationOutcome.Refused(
                DirectToolInvocationStatus.TimedOut, "The tool did not complete within its deadline.", sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            // Full detail stays in the structured log. Tool and infrastructure exceptions carry host
            // paths, connection strings and container internals, and this response crosses a trust
            // boundary — so the caller gets a stable code and nothing derived from the exception.
            _logger.LogError(ex, "Direct invocation of {ToolName} threw", toolName);
            return DirectToolInvocationOutcome.Refused(
                DirectToolInvocationStatus.Faulted, DirectToolInvocationErrors.Failed, sw.Elapsed);
        }
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

        var result = await tool
            .ExecuteAsync(armed.Request.Operation, armed.Request.Parameters, deadline.Token)
            .ConfigureAwait(false);

        sw.Stop();
        return Shape(result, armed, admission, sw.Elapsed);
    }

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
