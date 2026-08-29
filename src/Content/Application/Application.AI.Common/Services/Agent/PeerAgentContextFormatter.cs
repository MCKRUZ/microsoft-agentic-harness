using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Context;

namespace Application.AI.Common.Services.Agent;

/// <summary>
/// Formats one delegatable peer agent's description for injection into a turn's instructions —
/// the single formatter <see cref="PeerAgentContextProvider"/> (to build the actual prompt text) and
/// <see cref="Categorization.RegistrationBreakdownCalculator"/> (to size the <c>Agents</c> lane) both
/// call, so the two numbers describing one turn cannot drift apart into two independent estimates of
/// "how many tokens is this description" that happen to agree by coincidence (#518).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Deliberately no separate section header.</strong> Each entry is self-contained — id, name,
/// description, one line — with no wrapping title the provider would prepend once. A header would
/// read a little more polished, but its cost would sit entirely outside this formatter and therefore
/// outside <c>Categorization.RegistrationBreakdownCalculator.TokensFor(AgentRegistration)</c>'s per-entry sum,
/// reopening the exact gap #518 exists to close: text the model receives that no lane charges for.
/// The model already learns how to use an id from here — <c>delegate_task</c>'s own tool description
/// (Tools lane, already charged) explains the <c>target_agent</c> parameter once; this block only
/// needs to enumerate which ids are valid.
/// </para>
/// </remarks>
public static class PeerAgentContextFormatter
{
    /// <summary>
    /// Formats one peer's entry: its id (the value <c>delegate_task</c>'s <c>target_agent</c> parameter
    /// accepts), name, and description. Also what
    /// <c>Categorization.RegistrationBreakdownCalculator.TokensFor(AgentRegistration)</c> sizes.
    /// </summary>
    public static string FormatEntry(AgentRegistration agent) =>
        $"- \"{agent.Id}\" ({agent.Name}): {agent.Description}";

    /// <summary>
    /// Formats the full peer-agent block for injection — one line per peer, in the order given.
    /// </summary>
    /// <param name="peers">The delegatable peers for this turn, already self-excluded by the caller.</param>
    /// <returns>
    /// The joined block, or <see langword="null"/> when <paramref name="peers"/> is empty — meaning
    /// "contribute nothing", which <see cref="PeerAgentContextProvider"/> turns into an empty
    /// <c>AIContext</c> rather than injecting a block describing zero peers.
    /// </returns>
    public static string? FormatBlock(IReadOnlyList<AgentRegistration> peers) =>
        peers.Count == 0 ? null : string.Join("\n", peers.Select(FormatEntry));

    /// <summary>
    /// Resolves the delegatable peers for <paramref name="owningAgentId"/> from the full registry,
    /// self-excluded — the shared "who are this agent's peers" projection used by both
    /// <see cref="PeerAgentContextProvider"/> (to build the prompt block) and
    /// <c>ExecuteAgentTurnCommandHandler.BuildRegistrationSnapshot</c> (to size the context bar's
    /// <c>Agents</c> lane from the same registrations), so the self-exclusion rule lives in one place
    /// rather than two independently-written copies of the same filter.
    /// </summary>
    /// <param name="registry">Discovers the full set of registered agents.</param>
    /// <param name="owningAgentId">
    /// The calling agent's own id, excluded from its own peer list. <see langword="null"/> excludes
    /// nothing — a caller with no owning agent (a bare skill invocation) is not itself a registered
    /// peer, so there is nothing of its own to filter out.
    /// </param>
    public static List<AgentRegistration> GetPeers(IAgentMetadataRegistry registry, string? owningAgentId) =>
        registry.GetAll()
            .Where(a => !string.Equals(a.Id, owningAgentId, StringComparison.OrdinalIgnoreCase))
            .Select(a => new AgentRegistration(a.Id, a.Name, a.Description))
            .ToList();
}
