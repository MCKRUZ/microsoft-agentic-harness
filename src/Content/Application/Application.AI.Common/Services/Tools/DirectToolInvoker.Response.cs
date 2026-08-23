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
    /// <para>
    /// <strong>Known residual, stated rather than papered over.</strong> The pre-cut can bisect a
    /// secret at <c>ceiling + margin</c>, leaving a prefix the sanitizers cannot match. That prefix
    /// normally sits beyond the ceiling and is discarded by the final cut — but redaction shrinks text,
    /// so if net shrinkage across the scanned region exceeds this margin the prefix can migrate below
    /// the ceiling and be returned. Re-scrubbing the result does not fix it (a partial pattern still
    /// does not match), and no cheap check distinguishes a migrated prefix from ordinary content, so
    /// the honest mitigation is the margin being large relative to plausible shrinkage. Removing the
    /// class entirely means not pre-cutting at all, which costs a full sanitizer pass over an
    /// arbitrarily large tool result on a remotely-triggered path — the trade this constant exists to
    /// make. Revisit if a sanitizer is added whose replacements are much shorter than what they match.
    /// </para>
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
        ToolCallAdmission admission,
        TimeSpan duration)
    {
        var ceiling = armed.Config.MaxOutputCharacters;

        if (!result.Success)
        {
            var rawError = result.Error ?? "The tool reported a failure.";

            // Classification-gate redaction applies here too, not just to a success. A call the gate
            // flagged as touching sensitive data can fail with that data embedded in its own error
            // text (a connection string, a stack trace carrying an API key) exactly as easily as it
            // can succeed with it in its output — code review on the PR that added #479/#484's
            // guarantees to the success path below caught that this branch never picked them up.
            var (preCutError, _) = PreCutForScrub(rawError, ceiling);
            if (!armed.AdmissionPipeline.TryApplyTextOutputPolicy(admission, armed.ToolName, preCutError, out var admittedError))
            {
                return DirectToolInvocationOutcome.Refused(
                    DirectToolInvocationStatus.Denied,
                    GovernanceDenials.NotPermitted(armed.ToolName),
                    duration);
            }

            // No ErrorTruncated outcome field exists to report a drop on, matching prior behavior.
            var (error, _) = ScrubAndBound(admittedError ?? string.Empty, ceiling, armed.ToolName);
            return new DirectToolInvocationOutcome
            {
                Status = DirectToolInvocationStatus.ToolFailed,
                Error = error,
                Duration = duration
            };
        }

        var raw = result.Output ?? string.Empty;

        // #479: TryApplyTextOutputPolicy now sanitizes unconditionally on both branches, via the
        // admission pipeline's OWN sanitizer — a distinct instance from this class's _sanitizer field
        // in general (they happen to be the same singleton in every real composition, but this class
        // does not assume that). Pre-cutting here bounds what THAT sanitizer has to scan, for the same
        // reason ScrubAndBound below always pre-cuts before ITS sanitizer scans: a tool returning 20 MB
        // against a 256 KiB ceiling should not pay to scan all 20 MB twice over to return a fraction.
        //
        // droppedByPreCut is carried forward rather than discarded: content this pre-cut drops can
        // still leave ScrubAndBound's own (second) pre-cut with nothing further to drop — the text it
        // sees is already within the ceiling+margin — so its own flag alone would under-report what was
        // actually lost.
        var (preCut, droppedByPreCut) = PreCutForScrub(raw, ceiling);

        // Fails closed: when a redaction was required and could not be applied, the chain returns
        // false and the original must be withheld rather than emitted. See the chain's own remarks
        // for why falling back to the raw content is the trap.
        if (!armed.AdmissionPipeline.TryApplyTextOutputPolicy(admission, armed.ToolName, preCut, out var admitted))
        {
            return DirectToolInvocationOutcome.Refused(
                DirectToolInvocationStatus.Denied,
                GovernanceDenials.NotPermitted(armed.ToolName),
                duration);
        }

        // This class's own unconditional caller-facing scrub, via _sanitizer — the same treatment the
        // failure branch above applies to result.Error, and independent of whatever the admission
        // pipeline's sanitizer already did above.
        var (output, truncatedByOwnScrub) = ScrubAndBound(admitted ?? string.Empty, ceiling, armed.ToolName);
        var truncated = droppedByPreCut || truncatedByOwnScrub;

        return new DirectToolInvocationOutcome
        {
            Status = DirectToolInvocationStatus.Succeeded,
            Output = output,
            OutputTruncated = truncated,
            Duration = duration
        };
    }

    /// <summary>
    /// Redacts if the classification gate said to, sanitizes unconditionally, then bounds the length —
    /// this class's own unconditional treatment, via <see cref="_sanitizer"/>. Used for both halves of
    /// a <see cref="ToolResult"/>: an error string is as capable of being enormous, or of carrying a
    /// secret, as an output one.
    /// </summary>
    /// <param name="text">The raw text from the tool.</param>
    /// <param name="ceiling">The maximum number of characters to return, inclusive of any marker.</param>
    /// <param name="toolName">Passed to the sanitizers as context.</param>
    /// <returns>The safe text, and whether anything was dropped to produce it.</returns>
    private (string Text, bool Truncated) ScrubAndBound(string text, int ceiling, string toolName)
    {
        var (preCut, droppedBeforeScrubbing) = PreCutForScrub(text, ceiling);
        var scrubbed = Scrub(preCut, toolName);
        return FinalCut(scrubbed, ceiling, droppedBeforeScrubbing);
    }

    /// <summary>
    /// Cuts <paramref name="text"/> down to a scan-cost-bounded region before it reaches a sanitizer,
    /// so a tool returning far more than <paramref name="ceiling"/> allows does not pay to scan all of
    /// it to return a fraction. Extracted from <see cref="ScrubAndBound"/> so <see cref="Shape"/> can
    /// apply the same pre-cut ahead of the admission pipeline's own
    /// <see cref="Application.AI.Common.Interfaces.Governance.IToolCallAdmissionPipeline.TryApplyTextOutputPolicy"/>
    /// call, which performs its own, separate sanitize/redact pass (#479).
    /// </summary>
    /// <returns>The (possibly cut) text, and whether anything was cut.</returns>
    private static (string Text, bool Dropped) PreCutForScrub(string text, int ceiling)
    {
        // The margin is what makes cutting before scrubbing safe: a secret straddling the ceiling stays
        // inside the scanned region, so it is still redacted rather than sliced in half and emitted.
        // FinalCut removes the margin again.
        //
        // Saturating rather than wrapping. The validator bounds MaxOutputCharacters so this cannot
        // overflow from configuration, but the arithmetic should not depend on a check in another
        // assembly to stay correct — a negative slice length here turns a successful tool call into a
        // 500 for every caller.
        var scanCeiling = ceiling <= int.MaxValue - ScrubOverlapMargin
            ? ceiling + ScrubOverlapMargin
            : int.MaxValue;

        var dropped = text.Length > scanCeiling;
        return (dropped ? text[..scanCeiling] : text, dropped);
    }

    /// <summary>
    /// Bounds already-sanitized text to <paramref name="ceiling"/> characters via the shared
    /// <see cref="Governance.BoundedText.Cap"/> primitive (#467/#470), and folds in whether
    /// <see cref="PreCutForScrub"/> already dropped anything ahead of sanitizing.
    /// </summary>
    private static (string Text, bool Truncated) FinalCut(string scrubbed, int ceiling, bool droppedBeforeScrubbing)
    {
        // Re-checked rather than inferred from the pre-cut: scrubbing changes length in both directions
        // (a placeholder is rarely the width of what it replaced), so whether the ceiling is still
        // exceeded is only knowable now.
        var (bounded, cutAfterScrubbing) = Governance.BoundedText.Cap(scrubbed, ceiling, TruncationMarker);

        // Reported from what was actually dropped, never from what the raw length suggested. A string
        // barely over the ceiling that scrubbing shortened below it lost nothing, and claiming
        // otherwise would send a caller looking for content that is all present.
        return (bounded, droppedBeforeScrubbing || cutAfterScrubbing);
    }

    /// <summary>Runs text through the response-sanitizer chain. Empty text is returned untouched.</summary>
    private string Scrub(string content, string toolName) =>
        string.IsNullOrEmpty(content) ? content : _sanitizer.Sanitize(content, toolName).SanitizedContent;
}
