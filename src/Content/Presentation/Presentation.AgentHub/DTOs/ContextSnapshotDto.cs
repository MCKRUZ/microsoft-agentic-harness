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
/// <param name="MeasuredInputTokens">
/// What the provider actually billed for this turn's prompt, or <c>0</c> when it reported nothing.
/// Ground truth against which <paramref name="CtxAfter"/> can be reconciled.
/// </param>
/// <param name="UnaccountedTokens">
/// <paramref name="MeasuredInputTokens"/> minus the sum of <paramref name="CtxAfter"/>. Positive means
/// context reached the model that no category explains; <strong>negative means the breakdown's
/// estimates overshot what was billed</strong>, which is why this travels as its own signed number
/// rather than as a seventh category — a category is an additive token count and cannot be negative.
/// </param>
/// <remarks>
/// Both fields are additive to the wire contract: a client that ignores them renders exactly as
/// before. They are sent pre-computed rather than left for the client to derive so that every
/// consumer — dashboard, stored snapshot rows, any future reader — agrees on the arithmetic instead
/// of each reimplementing it.
/// </remarks>
public sealed record ContextSnapshotDto(
    string ConversationId,
    int TurnIndex,
    string TurnId,
    CategoryBreakdownDto CtxAfter,
    IReadOnlyList<LoadedItemDto> Loaded,
    DateTimeOffset CapturedAtUtc,
    int MeasuredInputTokens = 0,
    int UnaccountedTokens = 0);
