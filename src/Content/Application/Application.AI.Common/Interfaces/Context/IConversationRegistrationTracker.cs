namespace Application.AI.Common.Interfaces.Context;

/// <summary>
/// Per-conversation memory of which registrations (system prompt, skills, native tools,
/// MCP tools, sub-agents) the model has already seen, so per-turn context snapshots can
/// emit a clickable <c>LoadedItem</c> only the turn each registration first appears.
/// Subsequent turns emit only the message delta — matching the wire contract that
/// <c>loaded[]</c> is a per-turn delta the dashboard accumulates as the user scrubs.
/// </summary>
public interface IConversationRegistrationTracker
{
    /// <summary>
    /// Diffs <paramref name="current"/> against the last-known snapshot for
    /// <paramref name="conversationId"/>, returns what's new, and atomically updates
    /// the stored snapshot so the next call sees this turn's state as the baseline.
    /// On the first call for a conversation the entire snapshot is reported as new.
    /// </summary>
    RegistrationDelta DiffAndUpdate(string conversationId, RegistrationSnapshot current);

    /// <summary>
    /// Clears tracked state for the conversation. Mirrors <c>IAgentConversationCache.Evict</c>.
    /// </summary>
    void Evict(string conversationId);
}

/// <summary>
/// Snapshot of every registration in scope for a turn. <see cref="SystemPromptText"/>
/// is the merged instruction text the agent will send as its system message; skills
/// carry their own <c>Instructions</c> body so the per-skill token count can be
/// computed independent of the base instruction.
/// </summary>
public sealed record RegistrationSnapshot(
    string? SystemPromptText,
    IReadOnlyList<SkillRegistration> Skills,
    IReadOnlyList<ToolRegistration> NativeTools,
    IReadOnlyList<ToolRegistration> McpTools,
    IReadOnlyList<AgentRegistration> SubAgents);

/// <summary>Delta returned by the tracker — items that appeared this turn.</summary>
public sealed record RegistrationDelta(
    bool SystemPromptIsNew,
    IReadOnlyList<SkillRegistration> NewSkills,
    IReadOnlyList<ToolRegistration> NewNativeTools,
    IReadOnlyList<ToolRegistration> NewMcpTools,
    IReadOnlyList<AgentRegistration> NewSubAgents);

/// <summary>A skill in scope for the turn. <see cref="InstructionsText"/> is the
/// Tier-2 instruction body; sized via <c>TokenEstimationHelper</c>.</summary>
/// <param name="Id">Stable skill id.</param>
/// <param name="Name">Display name.</param>
/// <param name="InstructionsText">The Tier-2 instruction body.</param>
/// <param name="InlinedInPrompt">
/// Whether <paramref name="InstructionsText"/> is actually part of the agent's system prompt this
/// turn. <see langword="false"/> for a skill the framework serves on demand — its body is
/// deliberately kept out of the prompt and reaches the model only if the skill is loaded.
/// </param>
/// <remarks>
/// Being in scope is not the same as being in the prompt, and the distinction has to travel with the
/// registration because nothing downstream can recover it: the instruction is one opaque string that
/// does not say which bodies were folded in. Sizing the prompt as "instruction minus every registered
/// skill" therefore subtracts text that was never there — under-reporting the system prompt and
/// charging the skills lane for content the model never received (#507). Defaults to
/// <see langword="true"/> so a producer that does not know stays on the old, conservative reading
/// rather than silently zeroing the lane.
/// </remarks>
public sealed record SkillRegistration(
    string Id,
    string Name,
    string? InstructionsText,
    bool InlinedInPrompt = true);

/// <summary>A tool registration. <see cref="SchemaText"/> is the serialized JSON
/// schema (or null when unavailable); sized via <c>TokenEstimationHelper</c>.</summary>
public sealed record ToolRegistration(string Name, string? Description, string? SchemaText);

/// <summary>A delegatable sub-agent. <see cref="Description"/> sized for tokens.</summary>
public sealed record AgentRegistration(string Id, string Name, string? Description);
