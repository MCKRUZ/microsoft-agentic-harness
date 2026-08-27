using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Governance;
using Domain.AI.Models;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// Response shaping: turning a <see cref="ToolResult"/> into something safe to hand to a caller
/// outside the process.
/// </summary>
/// <remarks>
/// Split from the arming path because this is the trust boundary and it has its own rule — nothing
/// crosses it unscrubbed or unbounded. Keeping it in one place is what makes that claim checkable
/// rather than a property spread across several return statements.
/// </remarks>
public sealed partial class DirectToolInvoker
{
    /// <summary>Marker appended to a truncated result so the cut is visible in the payload itself.</summary>
    private const string TruncationMarker = "\n…[output truncated]";

    /// <summary>
    /// Redacts if the classification gate said to and sanitizes unconditionally — both now owned
    /// entirely by <see cref="IToolCallAdmissionPipeline.TryApplyTextOutputPolicy"/> (#487/#489/#490),
    /// which pre-cuts, sanitizes, redacts, and bounds to its own ceiling in one place — then applies
    /// this caller's own <paramref name="ceiling"/> on top, which may be stricter than the pipeline's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both halves of a <see cref="ToolResult"/> or MCP result go through the same treatment. A tool's
    /// error text is the likeliest place for a path or a connection string to surface, and an error
    /// string is as capable of being enormous as an output one — treating only success as sensitive, or
    /// only success as large, would leave the more dangerous half unhandled on both counts.
    /// </para>
    /// <para>
    /// This class used to run its own second sanitize pass and its own pre-cut/final-cut pair after the
    /// pipeline's — <c>ScrubAndBound</c>/<c>PreCutForScrub</c>/<c>Scrub</c>/<c>FinalCut</c>, all retired
    /// by this method. The second sanitize pass was pure duplicated cost (#489: the two sanitizer
    /// instances are the same singleton in every real composition), and the second pre-cut/final-cut
    /// pair duplicated exactly what the pipeline's own pre-cut/final-cut now does (#487/#493). What
    /// remains here is the one thing the pipeline cannot do on this class's behalf: apply a
    /// caller-specific ceiling that may be stricter than the pipeline's own. That final cut is a pure
    /// length operation over text the pipeline already sanitized and redacted, never a re-scan, so it
    /// does not reopen the unbounded-scan cost #487 fixed.
    /// </para>
    /// </remarks>
    private static DirectToolInvocationOutcome ShapeText(
        string? failureText,
        string successText,
        string toolName,
        IToolCallAdmissionPipeline admissionPipeline,
        ToolCallAdmission admission,
        int ceiling,
        TimeSpan duration,
        ILogger logger)
    {
        if (failureText is not null)
        {
            // Classification-gate redaction applies here too, not just to a success. A call the gate
            // flagged as touching sensitive data can fail with that data embedded in its own error
            // text (a connection string, a stack trace carrying an API key) exactly as easily as it
            // can succeed with it in its output — code review on the PR that added #479/#484's
            // guarantees to the success path caught that this branch never picked them up.
            if (!admissionPipeline.TryApplyTextOutputPolicy(
                    admission, toolName, failureText, out var admittedError, out _))
            {
                // #491: this Denied is not a governance refusal in the usual sense — the tool DID run
                // and DID produce failure text; only the redaction of that text couldn't be applied.
                // Without this line the audit trail cannot tell "the tool never ran" (every other
                // Denied outcome) from "the tool ran, had side effects, and its failure text was
                // withheld" — an executed-vs-never-ran distinction the caller-facing status alone
                // cannot carry, since GovernanceDenials.NotPermitted is deliberately the same generic
                // text every gate returns.
                logger.LogWarning(
                    "Direct invocation of {ToolName} executed and failed, but its failure text could "
                    + "not be redacted; the result is withheld rather than returned unredacted.",
                    toolName);
                return DirectToolInvocationOutcome.Refused(
                    DirectToolInvocationStatus.Denied,
                    GovernanceDenials.NotPermitted(toolName),
                    duration);
            }

            // No ErrorTruncated outcome field exists to report a drop on, matching prior behavior.
            var (error, _) = Governance.BoundedText.Cap(admittedError ?? string.Empty, ceiling, TruncationMarker);
            return new DirectToolInvocationOutcome
            {
                Status = DirectToolInvocationStatus.ToolFailed,
                Error = error,
                Duration = duration
            };
        }

        // Fails closed: when a redaction was required and could not be applied, the chain returns
        // false and the original must be withheld rather than emitted. See the chain's own remarks
        // for why falling back to the raw content is the trap.
        if (!admissionPipeline.TryApplyTextOutputPolicy(
                admission, toolName, successText, out var admitted, out var truncatedByPipeline))
        {
            return DirectToolInvocationOutcome.Refused(
                DirectToolInvocationStatus.Denied,
                GovernanceDenials.NotPermitted(toolName),
                duration);
        }

        var (output, truncatedByOwnCeiling) = Governance.BoundedText.Cap(admitted ?? string.Empty, ceiling, TruncationMarker);

        return new DirectToolInvocationOutcome
        {
            Status = DirectToolInvocationStatus.Succeeded,
            Output = output,
            OutputTruncated = truncatedByPipeline || truncatedByOwnCeiling,
            Duration = duration
        };
    }

    /// <summary>Reduces a keyed-DI <see cref="ToolResult"/> to <see cref="ShapeText"/>'s shared shape.</summary>
    private DirectToolInvocationOutcome Shape(
        ToolResult result,
        ArmedInvocation armed,
        ToolCallAdmission admission,
        TimeSpan duration) =>
        ShapeText(
            result.Success ? null : result.Error ?? "The tool reported a failure.",
            result.Output ?? string.Empty,
            armed.ToolName,
            armed.AdmissionPipeline,
            admission,
            armed.Config.MaxOutputCharacters,
            duration,
            _logger);
}
