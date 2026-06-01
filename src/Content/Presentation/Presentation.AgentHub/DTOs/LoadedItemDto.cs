namespace Presentation.AgentHub.DTOs;

/// <summary>
/// Wire shape for <see cref="Domain.AI.Context.LoadedItem"/>. <c>Cat</c> is
/// emitted as the lowercase enum name (e.g. <c>"messages"</c>) so it lines up
/// with the frontend <c>ContextCategory</c> string literal type.
/// </summary>
/// <param name="What">Human label shown in the per-turn drawer.</param>
/// <param name="Tokens">Tokens this item contributed.</param>
/// <param name="Cat">Lowercase category name — drives bar-segment colouring.</param>
/// <param name="Ref">Optional file / message reference the drawer can open.</param>
public sealed record LoadedItemDto(
    string What,
    int Tokens,
    string Cat,
    string? Ref);
