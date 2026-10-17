using System.Diagnostics;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Escalation;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// The admit, invoke, report sequence both direct-invocation surfaces run (#547): the keyed-DI tool path
/// and the MCP tool path differ in how a tool is resolved, invoked and told apart from a failure, and in
/// nothing else.
/// </summary>
/// <remarks>
/// This used to be written out twice, once per surface, and the two copies had already started to drift
/// (the keyed path passes a resource request into admission and the MCP path does not). The shared parts
/// — the one deadline over the whole chain, the loop-detection flag, the refusal on a denied verdict, the
/// close-the-approval-loop report on a fault, and the success/failure report — now live here, so a change
/// to any of them lands on both surfaces or on neither.
/// </remarks>
public sealed partial class DirectToolInvoker
{
    /// <summary>
    /// Admits the call under one deadline, invokes the tool, reports what happened to the approval loop,
    /// and shapes the result for a caller outside the process.
    /// </summary>
    /// <typeparam name="TRaw">What the surface's tool invocation returns before it is shaped.</typeparam>
    /// <param name="admissionPipeline">This invocation's scoped admission chain.</param>
    /// <param name="admissionRequest">
    /// The call to admit. Built by the surface because only the surface knows what resource request, if
    /// any, its tool can declare. Must not count toward loop detection: a single invocation has no
    /// sequence to evaluate.
    /// </param>
    /// <param name="effectiveTimeout">The deadline for the whole chain plus the tool.</param>
    /// <param name="reportedBy">The surface's own identifier on the approval-loop report.</param>
    /// <param name="sw">The invocation's stopwatch; stopped here, before shaping.</param>
    /// <param name="cancellationToken">The caller's token, linked into the deadline.</param>
    /// <param name="invoke">Runs the tool under the deadline token.</param>
    /// <param name="failureTextOf">
    /// The tool's raw failure text for a raw result, or <see langword="null"/> when it succeeded. Passed
    /// onward untreated: <c>ToolCallAdmissionPipeline.ReportExecutionAsync</c> sanitizes, redacts and
    /// bounds it once, at the chokepoint every reporting path funnels through (#460).
    /// </param>
    /// <param name="shape">Reduces the raw result to the outcome handed to the caller, after the stopwatch has stopped.</param>
    private async Task<DirectToolInvocationOutcome> AdmitInvokeAndReportAsync<TRaw>(
        IToolCallAdmissionPipeline admissionPipeline,
        ToolCallAdmissionRequest admissionRequest,
        TimeSpan effectiveTimeout,
        string reportedBy,
        Stopwatch sw,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<TRaw>> invoke,
        Func<TRaw, string?> failureTextOf,
        Func<TRaw, ToolCallAdmission, Task<DirectToolInvocationOutcome>> shape)
    {
        // The deadline covers the admission chain as well as the tool: authorization can consult a
        // policy engine, the classification gate can call a model, and a rule can escalate to a human.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(effectiveTimeout);

        var admission = await admissionPipeline.AdmitAsync(admissionRequest, deadline.Token).ConfigureAwait(false);

        if (!admission.IsAllowed)
        {
            // The chain's message is already scrubbed for consumption outside the host — rule ids,
            // paths and policy internals stay in the trace and the structured log.
            return Refused(DirectToolInvocationStatus.Denied, admission.DeniedMessage!, sw);
        }

        TRaw raw;
        try
        {
            raw = await invoke(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Reported before rethrowing so the deadline/cancellation/fault handling in
            // RunArmedCoreAsync is unchanged — this only adds the approval-loop close, never replaces
            // the caller-facing outcome.
            await ApprovalExecutionReporting
                .ReportCallDidNotCompleteAsync(admissionPipeline, admission, reportedBy)
                .ConfigureAwait(false);
            throw;
        }

        var failureText = failureTextOf(raw);
        await ReportExecutionAsync(
            admissionPipeline,
            admission,
            failureText is null
                ? new ToolExecutionReport(EscalationExecutionStatus.Succeeded, null, null)
                : new ToolExecutionReport(
                    EscalationExecutionStatus.Failed, failureText, null, ToolName: admissionRequest.ToolName),
            reportedBy)
            .ConfigureAwait(false);

        sw.Stop();
        return await shape(raw, admission).ConfigureAwait(false);
    }
}
