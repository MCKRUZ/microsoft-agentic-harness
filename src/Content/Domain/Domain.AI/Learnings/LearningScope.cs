using Domain.AI.KnowledgeGraph.Scoping;

namespace Domain.AI.Learnings;

/// <summary>
/// Defines the visibility scope for a learning entry using a 3-tier hierarchy:
/// agent-specific -> team-wide -> global. A learning scoped to agent "X" in team "T"
/// is visible only to agent X. A team-scoped learning is visible to all agents in
/// team T. A global learning is visible to all agents. An optional
/// <see cref="UserId"/> narrows any tier to a single user.
/// </summary>
/// <remarks>
/// Scope resolution during recall: if querying for agent "X" in team "T", the store
/// returns learnings scoped to X, learnings scoped to T, and global learnings --
/// merging all levels with deduplication by <see cref="LearningEntry.LearningId"/>.
/// At least one of <see cref="AgentId"/>, <see cref="TeamId"/>, or <see cref="IsGlobal"/>
/// must be set. Validation is enforced by <c>RememberCommandValidator</c> (section 06).
/// <para>
/// <see cref="UserId"/> is a user axis, not a fourth tier. Human-written learnings leave
/// it null and stay shared with every user of their tier. Lessons that work-memory
/// synthesis distils from one user's conversations set it to that user, so only that
/// user recalls them (#968). Recall applies the axis through <see cref="IsVisibleTo"/>.
/// </para>
/// </remarks>
public sealed record LearningScope
{
    /// <summary>Scopes the learning to a specific agent. Null means not agent-scoped.</summary>
    public string? AgentId { get; init; }

    /// <summary>Scopes the learning to a team of agents. Null means not team-scoped.</summary>
    public string? TeamId { get; init; }

    /// <summary>When true, the learning is visible to all agents regardless of team.</summary>
    public bool IsGlobal { get; init; }

    /// <summary>
    /// Narrows the agent/team/global tier to a single user. Null means the learning is
    /// shared with every user of its tier; when set, only that user recalls it.
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>
    /// Determines whether a caller may recall this learning on the user axis.
    /// </summary>
    /// <param name="callerUserId">The calling user's identity, or <c>null</c> when the caller is unknown.</param>
    /// <returns>
    /// <c>true</c> when the learning is not user-scoped, or when the caller is known and is
    /// the same user after canonicalization; otherwise <c>false</c>. An unknown caller never
    /// sees a user-scoped learning.
    /// </returns>
    public bool IsVisibleTo(string? callerUserId)
    {
        if (ScopeIdentity.Canonicalize(UserId) is null)
            return true;

        return ScopeIdentity.Canonicalize(callerUserId) is not null
            && ScopeIdentity.AreSame(UserId, callerUserId);
    }
}
