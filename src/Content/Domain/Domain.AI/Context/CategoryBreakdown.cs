namespace Domain.AI.Context;

/// <summary>
/// Tokens consumed per <see cref="ContextCategory"/> at a single point in time.
/// Mirrors the frontend <c>CategoryBreakdown</c> shape in
/// <c>src/Content/Presentation/Presentation.Dashboard/src/lib/categories.ts</c>.
/// Every Foresight visualization (hero context bar, table mini-bar, timeline
/// node, legend) groups tokens by these six fields.
/// </summary>
/// <param name="System">Tokens from the harness system prompt.</param>
/// <param name="Agents">Tokens from rules / agents.md / project context.</param>
/// <param name="Skills">Tokens from loaded skill instructions.</param>
/// <param name="Tools">Tokens from tool JSON Schema definitions.</param>
/// <param name="Mcp">Tokens from MCP server descriptions.</param>
/// <param name="Messages">Tokens from the running conversation transcript.</param>
public sealed record CategoryBreakdown(
    int System,
    int Agents,
    int Skills,
    int Tools,
    int Mcp,
    int Messages)
{
    /// <summary>An all-zero breakdown — useful as a starting accumulator.</summary>
    public static CategoryBreakdown Empty { get; } = new(0, 0, 0, 0, 0, 0);

    /// <summary>
    /// Sum of all six category totals. Implemented as a switch over the enum
    /// so adding a category in <see cref="ContextCategory"/> forces an update
    /// here at compile time rather than silently undercounting.
    /// </summary>
    public int Total => Get(ContextCategory.System)
                      + Get(ContextCategory.Agents)
                      + Get(ContextCategory.Skills)
                      + Get(ContextCategory.Tools)
                      + Get(ContextCategory.Mcp)
                      + Get(ContextCategory.Messages);

    /// <summary>Returns the token total for the given category.</summary>
    public int Get(ContextCategory category) => category switch
    {
        ContextCategory.System => System,
        ContextCategory.Agents => Agents,
        ContextCategory.Skills => Skills,
        ContextCategory.Tools => Tools,
        ContextCategory.Mcp => Mcp,
        ContextCategory.Messages => Messages,
        _ => 0,
    };

    /// <summary>
    /// Returns a new breakdown with <paramref name="tokens"/> added to the
    /// given category. Other categories unchanged.
    /// </summary>
    public CategoryBreakdown Add(ContextCategory category, int tokens) => category switch
    {
        ContextCategory.System => this with { System = System + tokens },
        ContextCategory.Agents => this with { Agents = Agents + tokens },
        ContextCategory.Skills => this with { Skills = Skills + tokens },
        ContextCategory.Tools => this with { Tools = Tools + tokens },
        ContextCategory.Mcp => this with { Mcp = Mcp + tokens },
        ContextCategory.Messages => this with { Messages = Messages + tokens },
        _ => this,
    };
}
