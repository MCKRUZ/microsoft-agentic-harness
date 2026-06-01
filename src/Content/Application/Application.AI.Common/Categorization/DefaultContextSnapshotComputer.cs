using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces.Context;
using Domain.AI.Context;
using Microsoft.Extensions.AI;

namespace Application.AI.Common.Categorization;

/// <summary>
/// Default <see cref="IContextSnapshotComputer"/>. Pure function — no I/O, no
/// state. Derives the per-category breakdown from data the turn handler
/// already has on hand.
/// </summary>
/// <remarks>
/// <para>
/// <b>PR 3 v1 (simplified).</b> <c>MemoizedPromptComposer</c> does not expose
/// the per-section token sizing through its public surface, so this version
/// derives the breakdown post-hoc:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <see cref="ContextCategory.Messages"/> = <see cref="TokenEstimationHelper.EstimateTokens(IReadOnlyList{ChatMessage})"/> over the post-turn history.
///     </description>
///   </item>
///   <item>
///     <description>
///       <see cref="ContextCategory.System"/> = <c>max(0, inputTokens − messages)</c> — everything in the input prompt that isn't the running transcript (system prompt scaffolding, tool schemas, any skill text — all lumped into the system bucket for v1).
///     </description>
///   </item>
///   <item>
///     <description>
///       <see cref="ContextCategory.Agents"/>, <see cref="ContextCategory.Skills"/>, <see cref="ContextCategory.Tools"/>, <see cref="ContextCategory.Mcp"/> all read 0. <c>ContextBar</c> on the dashboard skips zero-token segments visually, so the bar shows two segments (System + Messages) today. When the follow-up plumbs the section list out of the composer, this implementation expands to use <see cref="SystemPromptSectionCategorizer"/> and the segments light up with no contract change.
///     </description>
///   </item>
/// </list>
/// </remarks>
public sealed class DefaultContextSnapshotComputer : IContextSnapshotComputer
{
    /// <inheritdoc />
    public ContextSnapshot Compute(
        string conversationId,
        int turnIndex,
        string turnId,
        int inputTokens,
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<LoadedItem> turnLoaded,
        DateTimeOffset capturedAtUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        ArgumentException.ThrowIfNullOrEmpty(turnId);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(turnLoaded);

        var messageTokens = TokenEstimationHelper.EstimateTokens(history);
        var systemTokens = Math.Max(0, inputTokens - messageTokens);

        var ctxAfter = new CategoryBreakdown(
            System: systemTokens,
            Agents: 0,
            Skills: 0,
            Tools: 0,
            Mcp: 0,
            Messages: messageTokens);

        return new ContextSnapshot(
            ConversationId: conversationId,
            TurnIndex: turnIndex,
            TurnId: turnId,
            CtxAfter: ctxAfter,
            Loaded: turnLoaded,
            CapturedAtUtc: capturedAtUtc);
    }
}
