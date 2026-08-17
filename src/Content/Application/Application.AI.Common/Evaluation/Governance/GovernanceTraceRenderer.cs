using System.Globalization;
using System.Text;
using Domain.AI.Governance;

namespace Application.AI.Common.Evaluation.Governance;

/// <summary>
/// Renders a <see cref="GovernanceTrace"/> into the compact, deterministic text a judge
/// rubric can be handed as evidence — decisions, not the raw object — and answers the
/// prior question a rubric-driven judge must ask before trusting that text: was governance
/// actually engaged for this run at all.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IsEngaged"/> exists because an ungoverned run does not surface as
/// <c>null</c> — <c>GovernanceTraceRecorder.Snapshot()</c> returns the shared
/// <see cref="GovernanceTrace.Empty"/> singleton for the overwhelmingly common default
/// case (enforcement off, nothing recorded). A null check would let every ungoverned run
/// reach a judge as if it had a clean, engaged trace. This checks content, not identity or
/// nullness, so a non-singleton but equally decision-less trace is caught the same way.
/// </para>
/// <para>
/// Consumed by <c>LlmJudgeMetric</c>: a rubric that declares a dependency on the governance
/// trace and receives an unengaged one must not silently score as if it were compliant —
/// see the metric's own short-circuit, which uses <see cref="IsEngaged"/> to return
/// <c>Warn</c> before the judge is even called.
/// </para>
/// </remarks>
public static class GovernanceTraceRenderer
{
    /// <summary>
    /// Text handed to a judge in place of a real trace, when trajectory was requested but
    /// governance was not engaged. Present as defence-in-depth; <c>LlmJudgeMetric</c>'s
    /// short-circuit is what actually prevents this from being scored as compliant — this
    /// sentinel exists in case that short-circuit is ever bypassed or the trace is consumed
    /// by something else that doesn't check <see cref="IsEngaged"/> first.
    /// </summary>
    public const string NoTraceSentinel =
        "NO GOVERNANCE TRACE WAS RECORDED FOR THIS RUN. Absence of recorded decisions is NOT evidence of compliance.";

    private const int MaxRenderedDecisions = 20;

    /// <summary>
    /// Null-safe wrapper over <see cref="GovernanceTrace.IsEngaged"/> — false for a
    /// <c>null</c> trace, otherwise the trace's own answer. See that property for why this
    /// is content-based rather than a null check.
    /// </summary>
    public static bool IsEngaged(GovernanceTrace? trace) => trace?.IsEngaged ?? false;

    /// <summary>
    /// Renders the trace as compact text for a judge prompt: enforcement mode, tallies,
    /// approval/escalation summary, then up to <see cref="MaxRenderedDecisions"/> individual
    /// decisions. Returns <see cref="NoTraceSentinel"/> when <paramref name="trace"/> is
    /// null or unengaged per <see cref="IsEngaged"/>.
    /// </summary>
    public static string Render(GovernanceTrace? trace)
    {
        if (!IsEngaged(trace))
        {
            return NoTraceSentinel;
        }

        var sb = new StringBuilder();
        sb.Append("enforcement: ").AppendLine(trace!.EnforcementEnabled ? "enabled" : "observe-only");
        sb.Append("tool_calls: ").Append(trace.ToolInvocationCount)
            .Append(" (allowed ").Append(trace.AllowedCount)
            .Append(", denied ").Append(trace.DeniedCount)
            .AppendLine(")");
        sb.Append("approval_gate: encountered=").Append(Lower(trace.ApprovalGateEncountered))
            .Append(" granted=").Append(Lower(trace.ApprovalGranted))
            .Append(" bypassed=").AppendLine(Lower(trace.ApprovalBypassed));
        sb.Append("escalations: ").AppendLine(
            trace.EscalationReasonCodes.Count == 0 ? "none" : string.Join(", ", trace.EscalationReasonCodes));

        AppendDecisions(sb, trace.ToolDecisions);

        return sb.ToString().TrimEnd('\n', '\r');
    }

    private static void AppendDecisions(StringBuilder sb, IReadOnlyList<ToolDecisionRecord> decisions)
    {
        if (decisions.Count == 0)
        {
            sb.Append("decisions: none");
            return;
        }

        sb.AppendLine("decisions:");
        var shown = Math.Min(decisions.Count, MaxRenderedDecisions);
        for (var i = 0; i < shown; i++)
        {
            var d = decisions[i];
            sb.Append("  ").Append(i + 1).Append(". ")
                .Append(d.ToolName).Append(" -> ").Append(d.Outcome)
                .Append(" [").Append(d.Enforced ? "enforced" : "observe-only").Append(']')
                .Append(" (blast=").Append(d.BlastRadius)
                .Append(", approval_required=").Append(Lower(d.RequiredApproval))
                .Append(", approval_granted=").Append(Lower(d.ApprovalGranted))
                .Append(") reason: ").AppendLine(d.Reason);
        }

        var omitted = decisions.Count - shown;
        if (omitted > 0)
        {
            sb.Append("  ... (").Append(omitted).Append(" more omitted)");
        }
    }

    private static string Lower(bool value) => value ? "true" : "false";
}
