namespace Domain.AI.Context;

/// <summary>
/// One artifact that landed in the model's context this turn — a user message,
/// an assistant response, a tool result, a freshly-loaded skill, etc.
/// Powers the per-turn delta panel in the Foresight session view (HANDOFF §4.1).
/// </summary>
/// <param name="What">Human label rendered in the timeline row (e.g. "User message", "Tool: Read · BillingPipeline.cs").</param>
/// <param name="Tokens">Tokens this item added to the context window.</param>
/// <param name="Category">Which bar segment grows because of this item.</param>
/// <param name="Reference">Optional file or message name the drawer should open when this row is clicked.</param>
public sealed record LoadedItem(
    string What,
    int Tokens,
    ContextCategory Category,
    string? Reference);
