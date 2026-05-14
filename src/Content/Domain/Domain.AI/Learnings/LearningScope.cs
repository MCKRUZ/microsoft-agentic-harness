namespace Domain.AI.Learnings;

/// <summary>
/// Defines the visibility scope for a learning entry using a 3-tier hierarchy:
/// agent-specific -> team-wide -> global. A learning scoped to agent "X" in team "T"
/// is visible only to agent X. A team-scoped learning is visible to all agents in
/// team T. A global learning is visible to all agents.
/// </summary>
/// <remarks>
/// Scope resolution during recall: if querying for agent "X" in team "T", the store
/// returns learnings scoped to X, learnings scoped to T, and global learnings --
/// merging all levels with deduplication by <see cref="LearningEntry.LearningId"/>.
/// At least one of <see cref="AgentId"/>, <see cref="TeamId"/>, or <see cref="IsGlobal"/>
/// must be set. Validation is enforced by <c>RememberCommandValidator</c> (section 06).
/// </remarks>
public sealed record LearningScope
{
    /// <summary>Scopes the learning to a specific agent. Null means not agent-scoped.</summary>
    public string? AgentId { get; init; }

    /// <summary>Scopes the learning to a team of agents. Null means not team-scoped.</summary>
    public string? TeamId { get; init; }

    /// <summary>When true, the learning is visible to all agents regardless of team.</summary>
    public bool IsGlobal { get; init; }
}
