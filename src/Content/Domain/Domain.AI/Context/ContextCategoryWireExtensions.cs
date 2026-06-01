namespace Domain.AI.Context;

/// <summary>
/// Canonical mapping between <see cref="ContextCategory"/> and the lowercase
/// string used on the SignalR wire, in HTTP responses, in persisted snapshot
/// rows, and in the frontend <c>CategoryKey</c> type literal in
/// <c>src/Content/Presentation/Presentation.Dashboard/src/lib/categories.ts</c>.
/// </summary>
/// <remarks>
/// Single source of truth — both Infrastructure (PostgresObservabilityStore
/// serialization) and Presentation (SignalR + HTTP DTO mapping) consume this
/// helper. Adding a new <see cref="ContextCategory"/> requires updating both
/// switches here, but only here.
/// </remarks>
public static class ContextCategoryWireExtensions
{
    /// <summary>Lowercase wire string for a category.</summary>
    public static string ToWire(this ContextCategory category) => category switch
    {
        ContextCategory.System => "system",
        ContextCategory.Agents => "agents",
        ContextCategory.Skills => "skills",
        ContextCategory.Tools => "tools",
        ContextCategory.Mcp => "mcp",
        ContextCategory.Messages => "messages",
        _ => throw new ArgumentOutOfRangeException(
            nameof(category),
            category,
            "Unknown ContextCategory — add a case to ContextCategoryWireExtensions.ToWire."),
    };

    /// <summary>
    /// Parse a wire string back into a category. Unknown values fall back to
    /// <see cref="ContextCategory.System"/> — forward-compat path for persisted
    /// rows written by a newer deployment that an older one is rehydrating.
    /// </summary>
    public static ContextCategory FromWire(string wire) => wire switch
    {
        "system" => ContextCategory.System,
        "agents" => ContextCategory.Agents,
        "skills" => ContextCategory.Skills,
        "tools" => ContextCategory.Tools,
        "mcp" => ContextCategory.Mcp,
        "messages" => ContextCategory.Messages,
        _ => ContextCategory.System,
    };
}
