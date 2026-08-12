using Application.AI.Common.Interfaces.Governance;
using Domain.AI.Escalation;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// The one "the call threw before it could report its own outcome" shape shared by every governed
/// tool-execution surface that closes the #325 approval loop on a catch-and-rethrow path.
/// </summary>
/// <remarks>
/// Extracted because <see cref="DirectToolInvoker"/> and <see cref="GovernedAIFunction"/> each
/// independently arrived at the identical shape — literal reason string included — for the one
/// outcome neither can describe more specifically: the call started, threw, and neither the tool
/// result shape nor a caught exception says anything more useful than "it didn't finish."
/// </remarks>
internal static class ApprovalExecutionReporting
{
    /// <summary>The reason text reported when a tool call throws before it can report its own outcome.</summary>
    internal const string CallDidNotComplete = "the tool call did not complete";

    /// <summary>
    /// Reports the call Failed with <see cref="CallDidNotComplete"/>, on
    /// <see cref="CancellationToken.None"/> so the report cannot itself be why an approver never
    /// learns the call failed. Callers use this immediately before rethrowing — it never throws.
    /// </summary>
    internal static ValueTask ReportCallDidNotCompleteAsync(
        IToolCallAdmissionPipeline pipeline, ToolCallAdmission admission, string reportedBy) =>
        pipeline.ReportExecutionAsync(
            admission,
            new ToolExecutionReport(EscalationExecutionStatus.Failed, CallDidNotComplete, null),
            reportedBy, CancellationToken.None);
}
