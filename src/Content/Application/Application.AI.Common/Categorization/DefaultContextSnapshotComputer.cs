using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces.Context;
using Domain.AI.Context;
using Microsoft.Extensions.AI;

namespace Application.AI.Common.Categorization;

/// <summary>
/// Default <see cref="IContextSnapshotComputer"/>. Pure function — no I/O, no
/// state. Assembles the per-category breakdown from measurements the turn
/// handler already has on hand.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every category is measured from what was actually loaded.</b> The registration categories
/// (<see cref="ContextCategory.System"/>, <see cref="ContextCategory.Skills"/>,
/// <see cref="ContextCategory.Tools"/>, <see cref="ContextCategory.Mcp"/>,
/// <see cref="ContextCategory.Agents"/>) arrive already summed from the real system-prompt, skill,
/// tool-schema and peer-agent text; <see cref="ContextCategory.Messages"/> is estimated over
/// <c>history</c> as the caller passes it — the turn's own not-yet-sent response must not be in
/// there (#517; see <see cref="IContextSnapshotComputer.Compute"/>'s remarks on <c>history</c>).
/// </para>
/// <para>
/// <b>What changed, and why it was wrong before (#507).</b> This class used to compute
/// <c>System = max(0, inputTokens − messages)</c> — a residual, never measured, absorbing everything
/// the estimate failed to explain. Two consequences. A tool-heavy turn makes the transcript estimate
/// overshoot (the ~4-chars-per-token rule runs long on JSON-shaped tool payloads), the subtraction
/// goes negative, and the clamp reports <em>no system prompt at all</em> — indistinguishable from a
/// turn that genuinely had none, on a bar whose whole job is showing where context goes. And because
/// one bucket absorbed everything, the four segments beside it could only ever read zero, so the bar
/// has always shown two of its six lanes.
/// </para>
/// <para>
/// The provider's reported total feeds exactly one figure now, computed against operands pinned to
/// the same side of the turn boundary (#517): <see cref="ContextSnapshot.UnattributedTokens"/>. It
/// does not feed any <see cref="ContextCategory"/> — those remain pure measurements of what was
/// actually loaded, never a subtraction from a billed total. See that property's remarks for why this
/// is safe where #507's original, removed attempt was not.
/// </para>
/// </remarks>
public sealed class DefaultContextSnapshotComputer : IContextSnapshotComputer
{
    /// <inheritdoc />
    public ContextSnapshot Compute(
        string conversationId,
        int turnIndex,
        string turnId,
        IReadOnlyList<ChatMessage> history,
        CategoryBreakdown registrations,
        IReadOnlyList<LoadedItem> turnLoaded,
        DateTimeOffset capturedAtUtc,
        int? lastCallPromptTokens = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        ArgumentException.ThrowIfNullOrEmpty(turnId);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(turnLoaded);

        // Taken from the registrations rather than added to them: the transcript is measured here,
        // from history, and a caller that also populated Messages would otherwise have it counted
        // twice. Explicit `with` rather than trusting callers to pass zero — the double-count would
        // show up as a plausible-looking number, not as a failure.
        var ctxAfter = registrations with
        {
            Messages = TokenEstimationHelper.EstimateTokens(history),
        };

        return new ContextSnapshot(
            ConversationId: conversationId,
            TurnIndex: turnIndex,
            TurnId: turnId,
            CtxAfter: ctxAfter,
            Loaded: turnLoaded,
            CapturedAtUtc: capturedAtUtc,
            UnattributedTokens: lastCallPromptTokens is { } prompt ? prompt - ctxAfter.Total : null);
    }
}
