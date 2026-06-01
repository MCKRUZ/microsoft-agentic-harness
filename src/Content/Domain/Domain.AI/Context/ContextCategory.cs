namespace Domain.AI.Context;

/// <summary>
/// The six categories that compose the model's context window in the Foresight
/// observability surface. Wire format is the lowercase string of the name —
/// see <see cref="CategoryBreakdown"/> for the per-category token totals and
/// the Dashboard's <c>src/lib/categories.ts</c> for the matching frontend
/// taxonomy. Order is load-bearing: it drives the segmented context bar.
/// </summary>
public enum ContextCategory
{
    /// <summary>The harness system prompt (agent identity, session state, hooks).</summary>
    System,

    /// <summary>Loaded agents.md / rules-style policy text and CLAUDE.md user context.</summary>
    Agents,

    /// <summary>Loaded skills — SKILL.md bodies brought in via progressive disclosure.</summary>
    Skills,

    /// <summary>Tool JSON Schema definitions sent to the LLM.</summary>
    Tools,

    /// <summary>MCP server descriptions and remote tool surfaces.</summary>
    Mcp,

    /// <summary>The running transcript — user, assistant, and tool messages.</summary>
    Messages,
}
