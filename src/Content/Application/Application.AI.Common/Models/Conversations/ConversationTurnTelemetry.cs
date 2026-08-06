namespace Application.AI.Common.Models.Conversations;

/// <summary>
/// What one turn cost. The unit every transport hands to <c>IConversationTelemetryRecorder</c>.
/// </summary>
/// <param name="InputTokens">Prompt tokens billed for this turn.</param>
/// <param name="OutputTokens">Completion tokens billed for this turn.</param>
/// <param name="CacheRead">Tokens served from the provider's prompt cache.</param>
/// <param name="CacheWrite">Tokens written into the provider's prompt cache.</param>
/// <param name="CostUsd">The turn's cost in US dollars.</param>
/// <param name="ToolCalls">How many tools the turn invoked.</param>
/// <param name="Model">
/// The model that served the turn, or <see langword="null"/> to leave the session's recorded model as
/// it is. Carried per turn because a conversation can change model mid-flight.
/// </param>
/// <remarks>
/// Six numbers that always travel together, named once. They were previously spelled out as six
/// arguments at three call sites, inside a twelve-argument store call — which is three chances to
/// transpose two <c>int</c>s into columns that would still add up and still look plausible on a
/// dashboard.
/// </remarks>
public sealed record ConversationTurnTelemetry(
    int InputTokens,
    int OutputTokens,
    int CacheRead,
    int CacheWrite,
    decimal CostUsd,
    int ToolCalls,
    string? Model = null);
