using Domain.AI.Agents;
using Domain.AI.Governance;
using Domain.AI.Orchestration;

namespace Infrastructure.AI.Agents;

/// <summary>
/// Shared <see cref="AgentDefinition"/> → <see cref="AgentCandidate"/> mapping for every place that
/// builds a candidate representing a real, named <c>AGENT.md</c> agent (as opposed to a built-in
/// subagent archetype). Two independent call sites built this by hand before — one in
/// <c>CapabilityMatchSupervisor</c>, one in <c>Infrastructure.AI.Routing.AgentRouter</c> — with no
/// shared source, which is exactly the "the mapping drifts and nobody notices" shape this codebase
/// has hit before with other duplicated mapping sites.
/// </summary>
internal static class AgentCandidateMapping
{
    /// <summary>
    /// Builds the base candidate every named-agent call site needs: id, type, and an empty tool
    /// list (neither caller has a real tool list to offer at the point it builds this). The trust
    /// tier is deliberately still a caller-supplied parameter, not baked in here — each call site's
    /// choice reflects a different, already-documented reason (see each caller's own remarks), and
    /// unifying it would either force one to change its behavior or hide that the two are answering
    /// different questions.
    /// </summary>
    public static AgentCandidate FromDefinition(AgentDefinition agentDef, AutonomyLevel autonomyLevel) => new()
    {
        AgentId = agentDef.Id,
        AgentType = SubagentType.NamedAgent,
        AutonomyLevel = autonomyLevel,
        AvailableTools = []
    };
}
