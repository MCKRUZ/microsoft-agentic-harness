using Domain.AI.Context;
using Microsoft.Extensions.AI;

namespace Application.AI.Common.Interfaces.Context;

/// <summary>
/// Computes a <see cref="ContextSnapshot"/> at the end of an agent turn —
/// the per-category breakdown of what the model has in its context window
/// and the per-turn delta of what was added.
/// </summary>
/// <remarks>
/// <para>
/// Implementations MUST be pure functions of their inputs (no I/O, no clocks
/// other than what is passed in). The
/// <c>Application.Core.CQRS.Agents.ExecuteAgentTurn.ExecuteAgentTurnCommandHandler</c>
/// calls this once per turn after the assistant message has been recorded,
/// then passes the result to <see cref="IContextSnapshotNotifier"/>.
/// </para>
/// <para>
/// <strong>Every category is measured; none is a residual.</strong> The registration categories
/// (system prompt, skills, tools, MCP, peer agents) arrive already summed in
/// <c>registrations</c>, computed from the text actually loaded into the agent;
/// <see cref="ContextCategory.Messages"/> is estimated from the transcript. Nothing is derived by
/// subtracting from the provider's reported usage, which is what let one overshooting estimate floor
/// the whole system-prompt segment to zero (#507).
/// </para>
/// <para>
/// <strong><see cref="ContextSnapshot.UnattributedTokens"/> is the one figure that IS a subtraction</strong>,
/// and deliberately so (#517) — it exists to surface exactly the gap between the measured categories
/// and what the provider actually billed, not to replace any of them. It is safe now in a way #507's
/// original attempt was not: both operands are pinned to the same model call and the same side of the
/// turn boundary — see <c>Compute</c>'s <c>lastCallPromptTokens</c> and <c>history</c> parameters below.
/// </para>
/// </remarks>
public interface IContextSnapshotComputer
{
    /// <summary>
    /// Computes a single context snapshot for the just-completed turn.
    /// </summary>
    /// <param name="conversationId">Stable conversation identifier (matches the SignalR group).</param>
    /// <param name="turnIndex">Zero-based turn index within the conversation.</param>
    /// <param name="turnId">Stable turn identifier (e.g. <c>t-04</c>).</param>
    /// <param name="history">
    /// The message history as it stood for the turn's last model call — the user message and any
    /// prior-turn messages, but <strong>not</strong> this turn's own assistant response. That response
    /// is the call's <em>output</em>, never part of what was billed as input, so including it here
    /// would misalign <see cref="ContextCategory.Messages"/> against <paramref name="lastCallPromptTokens"/>
    /// by exactly one message every turn (#517) — the caller is responsible for passing the pre-response
    /// history, not appending the response first.
    /// </param>
    /// <param name="registrations">
    /// Cumulative, measured token totals for everything registered into the agent's context — the
    /// system prompt, each loaded skill, native tool schemas, MCP tool surfaces, and peer agents.
    /// <strong>Cumulative, not the per-turn delta:</strong> the snapshot's contract is the running
    /// state after this turn, so a caller passing only what changed would report a conversation
    /// steadily forgetting its own tools. Any <see cref="ContextCategory.Messages"/> total here is
    /// ignored — the transcript is measured from <paramref name="history"/>, and counting it twice is
    /// the obvious way to get this wrong.
    /// </param>
    /// <param name="turnLoaded">Per-turn delta items (user message, assistant message, tool results) the timeline should show under this turn.</param>
    /// <param name="capturedAtUtc">Server clock at capture time (stamped on the snapshot).</param>
    /// <param name="lastCallPromptTokens">
    /// The turn's last model call's own prompt size (input + cache-read + cache-write tokens for that
    /// one call — never the turn's accumulated total, which answers a different question; see
    /// <see cref="ContextSnapshot.UnattributedTokens"/>'s remarks). <see langword="null"/> when no
    /// model call landed this turn, in which case <see cref="ContextSnapshot.UnattributedTokens"/> is
    /// also null — there is nothing to reconcile against.
    /// </param>
    ContextSnapshot Compute(
        string conversationId,
        int turnIndex,
        string turnId,
        IReadOnlyList<ChatMessage> history,
        CategoryBreakdown registrations,
        IReadOnlyList<LoadedItem> turnLoaded,
        DateTimeOffset capturedAtUtc,
        int? lastCallPromptTokens = null);
}
