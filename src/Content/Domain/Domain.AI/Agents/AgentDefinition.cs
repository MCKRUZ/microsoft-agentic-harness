namespace Domain.AI.Agents;

/// <summary>
/// The first-class definition of an agent, parsed from its <c>AGENT.md</c> file. Holds the agent's
/// identity, categorisation, and source paths, plus its own instructions (the <c>AGENT.md</c> body),
/// its tool ceiling (<see cref="AllowedTools"/>), and the ids of the skills it composes. This is the
/// runtime representation of an agent — there is no separate, richer manifest type.
/// </summary>
/// <remarks>
/// Populated by <c>AgentMetadataParser</c> from an <c>AGENT.md</c> file and cached by
/// <c>IAgentMetadataRegistry</c>. It is the agent analogue of <see cref="Domain.AI.Skills.SkillDefinition"/>:
/// cheap to load and safe to hold in memory for every configured agent. The heavier per-turn work —
/// resolving skills, merging instructions, and provisioning tools — is done by the agent factory when
/// the agent is actually built, not stored here.
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
    /// Skill IDs that provide this agent's instructions, tool declarations, and behaviour.
    /// When empty, consumers should fall back to <see cref="Id"/> as a single skill ID.
    /// Populated from the <c>skills:</c> frontmatter list or the singular <c>skill:</c> entry
    /// in AGENT.md.
    /// </summary>
    public IReadOnlyList<string> Skills { get; init; } = [];

    /// <summary>
    /// The agent's own instructions — the markdown body of the <c>AGENT.md</c> file, below the YAML
    /// frontmatter. This is the agent's system prompt and leads the final instruction text, ahead of
    /// the instructions contributed by its skills. Null when the <c>AGENT.md</c> has no body.
    /// </summary>
    public string? Instructions { get; init; }

    /// <summary>
    /// The agent's tool ceiling — the allowlist declared in the <c>allowed-tools</c> frontmatter of
    /// <c>AGENT.md</c>. Acts as an upper bound: the agent may invoke a tool only when it appears here
    /// <em>and</em> is granted by one of its skills. The ceiling can only ever <em>tighten</em> the
    /// skills' combined allowlist, never widen it — a tool listed here but not granted by any skill
    /// stays unavailable. Empty when the agent declares no ceiling, in which case the skills' own
    /// allowlists govern unchanged. Populated from the <c>allowed-tools:</c> frontmatter list.
    /// </summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    /// <summary>Absolute path to the source <c>AGENT.md</c> file.</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>Directory containing the <c>AGENT.md</c> and its companion resources.</summary>
    public string BaseDirectory { get; init; } = string.Empty;

    /// <summary>Timestamp when this definition was loaded from disk.</summary>
    public DateTime LoadedAt { get; init; } = DateTime.UtcNow;
}
