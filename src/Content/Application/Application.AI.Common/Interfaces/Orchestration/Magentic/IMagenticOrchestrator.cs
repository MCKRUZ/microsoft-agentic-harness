using Domain.Common;

namespace Application.AI.Common.Interfaces.Orchestration.Magentic;

/// <summary>
/// Skill-level surface for invoking a Magentic orchestration through the
/// harness. Wraps MAF's <c>MagenticWorkflowBuilder</c> +
/// <c>MagenticOrchestrator</c> and adds three harness concerns the underlying
/// API does not own:
/// <list type="number">
/// <item><description>OTel spans per <c>documentation/architecture/magentic-spans.md</c> (subscribed off the public event stream — MAF's <c>MagenticOrchestrator</c> is <see langword="internal"/>).</description></item>
/// <item><description>HITL plan-review routed through <c>IEscalationService</c> instead of returning to the workflow caller.</description></item>
/// <item><description>State-changing replans routed through PR-2's <c>SubmitChangeProposalCommand</c> instead of executing directly.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// MAF's Magentic surface is still flagged experimental
/// (<c>#pragma warning disable MAAIW001</c>) at the 1.9.0 GA. The harness pins
/// instrumentation to the public event types only; a MAF bump that changes the
/// public event shape will surface as a failing integration test in
/// <c>Infrastructure.AI.Tests/Orchestration/Magentic/</c> rather than a runtime
/// surprise.
/// </para>
/// <para>
/// Returns a <see cref="Result{T}"/> rather than throwing — the workflow's
/// terminal error reason surfaces as <c>Result.Fail</c> with a stable
/// <c>magentic.*</c> code; the full exception is logged via structured logging.
/// </para>
/// </remarks>
public interface IMagenticOrchestrator
{
    /// <summary>
    /// Run a Magentic workflow to completion. Blocks until terminal state
    /// (satisfied, round-limit, reset-limit, or error). HITL plan-review pauses
    /// during the run are bridged into the escalation service transparently.
    /// </summary>
    /// <param name="request">The workflow input — manager, participants, task, limits, HITL flag.</param>
    /// <param name="ct">A cancellation token that aborts the orchestration.</param>
    /// <returns>The terminal workflow result with derived counters and final output.</returns>
    Task<Result<MagenticWorkflowResult>> RunAsync(MagenticWorkflowRequest request, CancellationToken ct);
}
