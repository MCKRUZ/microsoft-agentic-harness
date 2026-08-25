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
/// <see cref="ContextCategory.Messages"/> is estimated from the transcript. <c>inputTokens</c> is
/// recorded as ground truth for reconciliation via
/// <see cref="ContextSnapshot.UnaccountedTokens"/> — it is no longer subtracted from to invent a
/// category, which is what let one overshooting estimate floor the whole system-prompt segment to
/// zero (#507).
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
    /// <param name="inputTokens">
    /// Tokens the provider reported for this turn's prompt (from <c>ILlmUsageCapture</c>), or 0 when
    /// none was reported. Recorded as <see cref="ContextSnapshot.MeasuredInputTokens"/> so the gap
    /// against the attributed total stays visible; never used to derive a category.
    /// </param>
    /// <param name="history">The full message history including the user message and assistant response that landed this turn.</param>
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
    ContextSnapshot Compute(
        string conversationId,
        int turnIndex,
        string turnId,
        int inputTokens,
        IReadOnlyList<ChatMessage> history,
        CategoryBreakdown registrations,
        IReadOnlyList<LoadedItem> turnLoaded,
        DateTimeOffset capturedAtUtc);
}
