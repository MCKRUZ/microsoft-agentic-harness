namespace Domain.AI.Agents;

/// <summary>
/// Metadata projection of an <see cref="AgentManifest"/> used for agent discovery and enumeration.
/// Holds the identity, categorisation, and source-path fields needed to list agents in a UI or
/// select one for invocation, without loading the full manifest body, tool declarations, or workflow state.
/// </summary>
/// <remarks>
/// Populated by <c>AgentMetadataParser</c> from the YAML frontmatter of an <c>AGENT.md</c> file
/// and cached by <c>IAgentMetadataRegistry</c>. This is the agent analogue of
/// <see cref="Domain.AI.Skills.SkillDefinition"/> at the Level 1 (index-card) tier: cheap to load,
/// safe to hold in memory for every configured agent.
/// </remarks>
public sealed record AgentDefinition
{
    /// <summary>Unique identifier, typically derived from the agent folder name.</summary>
    public required string Id { get; init; }

    /// <summary>Display name of the agent (falls back to <see cref="Id"/> when frontmatter omits it).</summary>
    public required string Name { get; init; }

    /// <summary>Short description of the agent's purpose, suitable for a dropdown tooltip.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Primary category (e.g., <c>analysis</c>, <c>orchestration</c>).</summary>
    public string? Category { get; init; }

    /// <summary>Semantic domain (e.g., <c>research</c>, <c>orchestration</c>).</summary>
    public string? Domain { get; init; }

    /// <summary>Semantic version of the manifest.</summary>
    public string? Version { get; init; }

    /// <summary>Author of the manifest.</summary>
    public string? Author { get; init; }

    /// <summary>Free-form tags for flexible filtering.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>
    /// Id of the <see cref="Domain.AI.Skills.SkillDefinition"/> that provides this agent's
    /// instructions, tool declarations, and behaviour. When null, consumers that require a
    /// skill (e.g. the conversation hub) should fall back to <see cref="Id"/> or surface a
    /// configuration error — the AGENT.md is missing a <c>skill:</c> frontmatter entry.
    /// </summary>
    public string? Skill { get; init; }

    /// <summary>Absolute path to the source <c>AGENT.md</c> file.</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>Directory containing the <c>AGENT.md</c> and its companion resources.</summary>
    public string BaseDirectory { get; init; } = string.Empty;

    /// <summary>Timestamp when this definition was loaded from disk.</summary>
    public DateTime LoadedAt { get; init; } = DateTime.UtcNow;
}
