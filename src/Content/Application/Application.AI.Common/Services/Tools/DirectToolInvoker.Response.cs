using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Models;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// Response shaping: turning a <see cref="ToolResult"/> into something safe to hand to a caller
/// outside the process.
/// </summary>
/// <remarks>
/// Split from the arming path because this is the trust boundary and it has its own rule — nothing
/// crosses it unscrubbed. Keeping it in one place is what makes that claim checkable rather than a
/// property spread across several return statements.
/// </remarks>
public sealed partial class DirectToolInvoker
{
    /// <summary>Marker appended to a truncated result so the cut is visible in the payload itself.</summary>
    private const string TruncationMarker = "\n…[output truncated]";

    /// <summary>
    /// How much beyond the output ceiling is kept while scrubbing, so a secret straddling the cut is
    /// still inside the scanned region and is still redacted. Removed again by the final cut.
    /// </summary>
    /// <remarks>
    /// Sized generously against the longest thing the sanitizers look for — connection strings and
    /// PEM-armoured keys run to a few kilobytes — because the cost of being wrong in one direction is
    /// a few spare kilobytes scanned, and in the other it is an unredacted secret on the wire.
    /// </remarks>
    private const int ScrubOverlapMargin = 8 * 1024;

    /// <summary>
    /// Redacts if the classification gate said to, sanitizes unconditionally, then bounds the length.
    /// </summary>
    /// <remarks>
    /// Sanitization is applied to the failure message as well as the output. A tool's own error text is
    /// the likeliest place for a path or a connection string to surface, and it crosses the same
    /// boundary the output does — treating only success as sensitive would leave the more dangerous
    /// half unscrubbed.
    /// </remarks>
    private DirectToolInvocationOutcome Shape(
        ToolResult result,
        ArmedInvocation armed,
        ClassificationVerdict? classification,
        TimeSpan duration)
    {
        if (!result.Success)
        {
            return new DirectToolInvocationOutcome
            {
                Status = DirectToolInvocationStatus.ToolFailed,
                Error = Scrub(result.Error ?? "The tool reported a failure.", armed.ToolName),
                Duration = duration
            };
        }

        var ceiling = armed.Config.MaxOutputCharacters;
        var raw = result.Output ?? string.Empty;

        // Cut BEFORE scrubbing, not after. Redaction and the sanitizer chain are passes over the whole
        // string, and a tool returning 20 MB against a 256 KiB ceiling would otherwise pay to scan all
        // 20 MB in order to return a fraction of it — on a surface a remote caller triggers at will.
        //
        // The margin is what makes the reorder safe: a secret straddling the ceiling stays inside the
        // scanned region, so it is still redacted rather than sliced in half and emitted. The final cut
        // below removes the margin again.
        var scanCeiling = ceiling + ScrubOverlapMargin;
        var droppedBeforeScrubbing = raw.Length > scanCeiling;
        var output = droppedBeforeScrubbing ? raw[..scanCeiling] : raw;

        if (classification?.Outcome == ClassificationGateOutcome.RedactOutput && armed.ClassificationGate is not null)
            output = armed.ClassificationGate.RedactResult(armed.ToolName, output) as string ?? output;

        output = Scrub(output, armed.ToolName);

        // Re-checked rather than inferred from the pre-cut: scrubbing changes length in both directions
        // (a placeholder is rarely the width of what it replaced), so whether the ceiling is still
        // exceeded is only knowable now.
        var cutAfterScrubbing = output.Length > ceiling;
        if (cutAfterScrubbing)
            output = string.Concat(output.AsSpan(0, ceiling), TruncationMarker);

        // Reported from what was actually dropped, never from what the raw length suggested. A string
        // barely over the ceiling that scrubbing shortened below it lost nothing, and claiming
        // otherwise would send a caller looking for content that is all present.
        var truncated = droppedBeforeScrubbing || cutAfterScrubbing;

        return new DirectToolInvocationOutcome
        {
            Status = DirectToolInvocationStatus.Succeeded,
            Output = output,
            OutputTruncated = truncated,
            Duration = duration
        };
    }

    /// <summary>Runs text through the response-sanitizer chain. Empty text is returned untouched.</summary>
    private string Scrub(string content, string toolName) =>
        string.IsNullOrEmpty(content) ? content : _sanitizer.Sanitize(content, toolName).SanitizedContent;
}
