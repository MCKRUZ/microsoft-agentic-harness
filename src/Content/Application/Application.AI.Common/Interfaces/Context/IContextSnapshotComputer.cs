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
/// <see cref="ICompositionRoot.CQRS.Agents.ExecuteAgentTurn.ExecuteAgentTurnCommandHandler"/>
/// calls this once per turn after the assistant message has been recorded,
/// then passes the result to <see cref="IContextSnapshotNotifier"/>.
/// </para>
/// <para>
/// PR 3 v1 derives the breakdown post-hoc from the turn's
/// <paramref name="inputTokens"/> usage and the post-turn message history,
/// lumping the entire system-prompt area into <see cref="ContextCategory.System"/>.
/// A follow-up that plumbs <c>SystemPromptSection</c> data through will replace
/// the implementation without touching this contract.
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
    /// <param name="inputTokens">Tokens reported by the LLM for the input prompt this turn (from <c>ILlmUsageCapture</c>).</param>
    /// <param name="history">The full message history including the user message and assistant response that landed this turn.</param>
    /// <param name="turnLoaded">Per-turn delta items (user message, assistant message, tool results) the timeline should show under this turn.</param>
    /// <param name="capturedAtUtc">Server clock at capture time (stamped on the snapshot).</param>
    ContextSnapshot Compute(
        string conversationId,
        int turnIndex,
        string turnId,
        int inputTokens,
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<LoadedItem> turnLoaded,
        DateTimeOffset capturedAtUtc);
}
