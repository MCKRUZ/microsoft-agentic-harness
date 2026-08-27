using System.Diagnostics;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Governance;
using Domain.AI.Governance;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// The arm/authorize/catch skeleton and its supporting pieces — shared by both direct-invocation
/// surfaces (#494), split into its own partial so this file stays about one concern: getting a call
/// safely from "here is a tool name and a caller" to "the admission chain is armed, ready, and torn
/// down again no matter how the call ends."
/// </summary>
public sealed partial class DirectToolInvoker
{
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
    /// Everything <see cref="RunArmedCoreAsync"/> needs to arm one invocation, gathered into a single
    /// value rather than six positional parameters — found in review on #494: both call sites already
    /// needed named-argument syntax to stay readable, which is itself the signal that the list had
    /// grown too long for positional passing.
    /// </summary>
    /// <param name="ToolName">The tool being invoked, for the trace log and the deadline/fault log messages.</param>
    /// <param name="AgentId">The caller's synthetic identity — see <see cref="ArmGovernance"/>'s remarks.</param>
    /// <param name="Envelope">The caller's capability envelope to arm around the call.</param>
    /// <param name="EffectiveTimeout">
    /// The deadline this invocation runs under, already resolved by the caller from its request and
    /// the host's config — reported in the timeout log message, not enforced here; enforcement is the
    /// arming body's own concern. Deliberately not re-derived here from a request/config pair: the
    /// body needs the identical value for its own deadline token, and a second, independent derivation
    /// of "requested timeout or config default" is exactly the shape of bug where the logged deadline
    /// and the enforced one quietly drift apart after a future change to one derivation site and not
    /// the other.
    /// </param>
    /// <param name="TimeoutLogTemplate">
    /// The caller's own full message template for the deadline-timeout warning, e.g. this type's
    /// <see cref="DirectToolInvoker.TimeoutLogTemplate"/> — a complete template, not a prefix this
    /// method assembles into a shared one, so the structured log's message-template string (what a log
    /// backend groups and alerts by) stays exactly what each surface has always emitted. Must accept
    /// exactly the two placeholders <see cref="TimedOut"/> supplies (tool name, then timeout) in that
    /// order — nothing enforces that shape, so a future invocation surface with a differently-shaped
    /// template needs its own logging call, not a third template passed here.
    /// </param>
    /// <param name="FaultLogTemplate">
    /// The caller's own full message template for the fault-branch error, e.g. this type's
    /// <see cref="DirectToolInvoker.FaultLogTemplate"/> — same one-placeholder (tool name) constraint
    /// as <see cref="TimeoutLogTemplate"/>, enforced the same way: by convention, not the type system.
    /// </param>
    private readonly record struct ArmingRequest(
        string ToolName,
        string AgentId,
        Domain.AI.Bundles.CapabilityEnvelope Envelope,
        TimeSpan EffectiveTimeout,
        string TimeoutLogTemplate,
        string FaultLogTemplate);

    /// <summary>
    /// The arm/authorize/catch skeleton every direct-invocation surface runs, whatever kind of tool
    /// it turns out to be (#481) — extracted so <c>RunArmedAsync</c> and
    /// <c>DirectToolInvoker.Mcp.cs</c>'s <c>RunMcpArmedAsync</c> share the one implementation rather
    /// than each re-deriving it by hand (#494). <see cref="ArmGovernance"/> was already extracted for
    /// exactly this reason but stopped one level too shallow — the scope creation, the arm, the
    /// three-arm catch ladder, and the trace log around all of it were still duplicated.
    /// </summary>
    /// <param name="request">Everything this invocation needs to arm — see <see cref="ArmingRequest"/>.</param>
    /// <param name="cancellationToken">The caller's own token — see the first catch arm for why it
    /// alone distinguishes "the caller went away" from "the deadline elapsed".</param>
    /// <param name="body">
    /// Authorizes and runs the tool itself, given the armed pipeline, the invocation's DI scope (used
    /// by the keyed-DI path to resolve the tool; ignored by the MCP path, which already holds its
    /// <c>AIFunction</c>), and the shared stopwatch every outcome's <c>Duration</c> is measured against.
    /// </param>
    private async Task<DirectToolInvocationOutcome> RunArmedCoreAsync(
        ArmingRequest request,
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
                ArmGovernance(scope.ServiceProvider, request.AgentId, request.Envelope);
            using var _grantedEnvelope = grantedEnvelope;
            using var _armedAdmission = armedAdmission;

            try
            {
                return await body(admissionPipeline, scope, sw).ConfigureAwait(false);
            }
            finally
            {
                LogTrace(request.ToolName, request.AgentId, admissionPipeline.GetTrace);
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
            return TimedOut(sw, request.ToolName, request.EffectiveTimeout, request.TimeoutLogTemplate);
        }
        catch (Exception ex)
        {
            return Faulted(ex, sw, request.ToolName, request.FaultLogTemplate);
        }
    }

    /// <summary>Builds the timeout outcome and logs it. Split out of <see cref="RunArmedCoreAsync"/> to keep that method under this repo's function-length convention.</summary>
    private DirectToolInvocationOutcome TimedOut(
        Stopwatch sw, string toolName, TimeSpan effectiveTimeout, string logTemplate)
    {
        sw.Stop();
        _logger.LogWarning(logTemplate, toolName, effectiveTimeout);
        return DirectToolInvocationOutcome.Refused(
            DirectToolInvocationStatus.TimedOut, "The tool did not complete within its deadline.", sw.Elapsed);
    }

    /// <summary>
    /// Builds the fault outcome and logs it. Split out of <see cref="RunArmedCoreAsync"/> for the same
    /// reason as <see cref="TimedOut"/>. Full detail stays in the structured log — the caller gets a
    /// stable code and nothing derived from the exception, since this response crosses a trust boundary
    /// and tool/infrastructure exceptions carry host paths, connection strings and container internals.
    /// </summary>
    private DirectToolInvocationOutcome Faulted(Exception ex, Stopwatch sw, string toolName, string logTemplate)
    {
        sw.Stop();
        _logger.LogError(ex, logTemplate, toolName);
        return DirectToolInvocationOutcome.Refused(
            DirectToolInvocationStatus.Faulted, DirectToolInvocationErrors.Failed, sw.Elapsed);
    }

    /// <summary>
    /// Establishes the caller's identity and capability envelope, then arms the ambient admission
    /// chain — the fixed-order sequence every direct-invocation surface must run before its own
    /// tool-specific resolution, whatever kind of tool that turns out to be (#481). Extracted so the
    /// keyed-DI <see cref="ITool"/> path (<c>RunArmedAsync</c>) and the MCP path
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
}
