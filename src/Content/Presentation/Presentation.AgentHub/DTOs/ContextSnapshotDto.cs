namespace Presentation.AgentHub.DTOs;

/// <summary>
/// Wire shape for <see cref="Domain.AI.Context.ContextSnapshot"/>. Used for both
/// the SignalR <c>ContextSnapshot</c> event payload and the <c>snapshots[]</c>
/// array on the <c>/api/sessions/:id</c> response. Mirrors the frontend
/// <c>ContextSnapshotEvent</c> type.
/// </summary>
/// <param name="ConversationId">Conversation this snapshot belongs to.</param>
/// <param name="TurnIndex">Zero-based turn index.</param>
/// <param name="TurnId">Stable turn id (<c>t-NN</c>).</param>
/// <param name="CtxAfter">Cumulative per-category breakdown after this turn.</param>
/// <param name="Loaded">Per-turn delta items.</param>
/// <param name="CapturedAtUtc">Server clock at capture.</param>
/// <param name="UnattributedTokens">
/// Signed reconciliation gap (#517) — see <see cref="Domain.AI.Context.ContextSnapshot.UnattributedTokens"/>
/// for the full contract. <see langword="null"/> when no model call landed this turn.
/// </param>
public sealed record ContextSnapshotDto(
    string ConversationId,
    int TurnIndex,
    string TurnId,
    CategoryBreakdownDto CtxAfter,
    IReadOnlyList<LoadedItemDto> Loaded,
    DateTimeOffset CapturedAtUtc,
    int? UnattributedTokens);
