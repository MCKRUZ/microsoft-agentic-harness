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
    /// Both halves of a <see cref="ToolResult"/> go through the same treatment. A tool's error text is
    /// the likeliest place for a path or a connection string to surface, and an error string is as
    /// capable of being enormous as an output one — treating only success as sensitive, or only
    /// success as large, would leave the more dangerous half unhandled on both counts.
    /// </remarks>
    private DirectToolInvocationOutcome Shape(
        ToolResult result,
        ArmedInvocation armed,
        ClassificationVerdict? classification,
        TimeSpan duration)
    {
        var ceiling = armed.Config.MaxOutputCharacters;

        if (!result.Success)
        {
            var (error, _) = ScrubAndBound(result.Error ?? "The tool reported a failure.", ceiling, armed.ToolName);
            return new DirectToolInvocationOutcome
            {
                Status = DirectToolInvocationStatus.ToolFailed,
                Error = error,
                Duration = duration
            };
        }

        var raw = result.Output ?? string.Empty;

        if (classification?.Outcome == ClassificationGateOutcome.RedactOutput && armed.ClassificationGate is not null)
        {
            if (!TryRedact(armed, raw, out raw))
            {
                // Fail closed. The gate decided this asset must not be emitted as-is and we could not
                // apply its redaction, so the one thing we must not do is fall back to the original —
                // which is precisely what `RedactResult(...) as string ?? output` would have done for
                // any gate that returns a non-string. The shipped gate always answers with a string
                // here, so this is a guard against a consumer-supplied one; it is exactly the sort of
                // fallback that reads as harmless and silently defeats the control.
                return DirectToolInvocationOutcome.Refused(
                    DirectToolInvocationStatus.Denied,
                    GovernanceDenials.NotPermitted(armed.ToolName),
                    duration);
            }
        }

        var (output, truncated) = ScrubAndBound(raw, ceiling, armed.ToolName);

        return new DirectToolInvocationOutcome
        {
            Status = DirectToolInvocationStatus.Succeeded,
            Output = output,
            OutputTruncated = truncated,
            Duration = duration
        };
    }

    /// <summary>
    /// Applies the classification gate's redaction, reporting whether it produced usable text.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the gate returned a string, which is then the redacted content.
    /// <see langword="false"/> when it returned anything else — the caller must not emit the original.
    /// </returns>
    /// <remarks>
    /// <c>IToolClassificationGate.RedactResult</c> is typed <c>object? → object?</c> because the agent
    /// path passes it structured results it deliberately leaves alone. This path only ever hands it a
    /// string, so a non-string answer means a gate that did something unexpected — and on a redaction
    /// path an unexpected answer is a reason to withhold, not to shrug.
    /// </remarks>
    private bool TryRedact(ArmedInvocation armed, string content, out string redacted)
    {
        var result = armed.ClassificationGate!.RedactResult(armed.ToolName, content);

        if (result is string text)
        {
            redacted = text;
            return true;
        }

        _logger.LogWarning(
            "Classification gate returned a {ResultType} rather than a string when redacting output of "
            + "{ToolName}; the result is withheld rather than returned unredacted.",
            result?.GetType().Name ?? "null",
            armed.ToolName);

        redacted = string.Empty;
        return false;
    }

    /// <summary>
    /// Sanitizes text and bounds it to <paramref name="ceiling"/> characters.
    /// </summary>
    /// <param name="text">The raw text from the tool.</param>
    /// <param name="ceiling">The maximum number of characters to return, inclusive of any marker.</param>
    /// <param name="toolName">Passed to the sanitizers as context.</param>
    /// <returns>The safe text, and whether anything was dropped to produce it.</returns>
    private (string Text, bool Truncated) ScrubAndBound(string text, int ceiling, string toolName)
    {
        // Cut BEFORE scrubbing, not after. The sanitizer chain is a pass over the whole string, and a
        // tool returning 20 MB against a 256 KiB ceiling would otherwise pay to scan all 20 MB in
        // order to return a fraction of it — on a surface a remote caller triggers at will.
        //
        // The margin is what makes the reorder safe: a secret straddling the ceiling stays inside the
        // scanned region, so it is still redacted rather than sliced in half and emitted. The final cut
        // below removes the margin again.
        //
        // Saturating rather than wrapping. The validator bounds MaxOutputCharacters so this cannot
        // overflow from configuration, but the arithmetic should not depend on a check in another
        // assembly to stay correct — a negative slice length here turns a successful tool call into a
        // 500 for every caller.
        var scanCeiling = ceiling <= int.MaxValue - ScrubOverlapMargin
            ? ceiling + ScrubOverlapMargin
            : int.MaxValue;

        var droppedBeforeScrubbing = text.Length > scanCeiling;
        var scrubbed = Scrub(droppedBeforeScrubbing ? text[..scanCeiling] : text, toolName);

        // Re-checked rather than inferred from the pre-cut: scrubbing changes length in both directions
        // (a placeholder is rarely the width of what it replaced), so whether the ceiling is still
        // exceeded is only knowable now.
        var cutAfterScrubbing = scrubbed.Length > ceiling;
        if (cutAfterScrubbing)
        {
            // The marker counts against the ceiling rather than being added on top of it: a caller that
            // set the ceiling to satisfy a downstream size contract would otherwise receive a payload
            // slightly over the limit they asked for, which is the one thing such a ceiling exists to
            // prevent. A ceiling smaller than the marker drops the marker rather than overshoot — the
            // ceiling is the promise, and the truncation flag is what carries the meaning.
            scrubbed = ceiling > TruncationMarker.Length
                ? string.Concat(scrubbed.AsSpan(0, ceiling - TruncationMarker.Length), TruncationMarker)
                : scrubbed[..ceiling];
        }

        // Reported from what was actually dropped, never from what the raw length suggested. A string
        // barely over the ceiling that scrubbing shortened below it lost nothing, and claiming
        // otherwise would send a caller looking for content that is all present.
        return (scrubbed, droppedBeforeScrubbing || cutAfterScrubbing);
    }

    /// <summary>Runs text through the response-sanitizer chain. Empty text is returned untouched.</summary>
    private string Scrub(string content, string toolName) =>
        string.IsNullOrEmpty(content) ? content : _sanitizer.Sanitize(content, toolName).SanitizedContent;
}
