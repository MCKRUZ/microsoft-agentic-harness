using Application.AI.Common.Interfaces;
using Microsoft.Agents.AI;

namespace Application.AI.Common.Services.Agent;

/// <summary>
/// An <see cref="AIContextProvider"/> that injects the descriptions of this agent's delegatable peers
/// into its instructions every turn — closing #518: before this provider, the context bar's
/// <c>Agents</c> lane charged tokens for peer descriptions no prompt ever contained, because nothing
/// put them there.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a provider, not the static system prompt.</strong> Baking peer text into the assembled
/// instruction string would charge it to the <c>System</c> lane and require subtracting it back out
/// of that lane's arithmetic — the same delicate machinery <c>RegistrationBreakdownCalculator</c>
/// already carries for skills — and would freeze the peer list at agent-construction time, which is
/// cached and never invalidated for the life of the process. A provider re-evaluates every turn (a
/// peer registered after this agent was built is picked up for free) and keeps the accounting
/// entirely inside the <c>Agents</c> lane, which already exists for exactly this content.
/// </para>
/// <para>
/// Resolves <see cref="IAgentMetadataRegistry"/> at construction, not per turn — it is a singleton
/// (discovery happens once, at host startup) with no per-request state, so unlike the two recall
/// providers this needs no <see cref="IAmbientRequestScope"/> bridge.
/// </para>
/// </remarks>
public sealed class PeerAgentContextProvider : AIContextProvider
{
    private readonly IAgentMetadataRegistry _agentRegistry;
    private readonly string? _owningAgentId;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerAgentContextProvider"/> class.
    /// </summary>
    /// <param name="agentRegistry">Discovers the full set of registered peer agents.</param>
    /// <param name="owningAgentId">
    /// This agent's own id — excluded from its own peer list so an agent is never offered itself as a
    /// delegation target. <see langword="null"/> when this agent has no owning <c>AGENT.md</c> (a bare
    /// skill invocation or a caller-curated orchestrator), in which case this provider injects
    /// <em>nothing</em> — see <see cref="PeerAgentContextFormatter.GetPeers"/>'s own remarks for why
    /// "exclude nothing" is unsafe here (it was this provider's own #518 correctness-review defect).
    /// </param>
    public PeerAgentContextProvider(IAgentMetadataRegistry agentRegistry, string? owningAgentId)
    {
        ArgumentNullException.ThrowIfNull(agentRegistry);

        _agentRegistry = agentRegistry;
        _owningAgentId = owningAgentId;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns <em>only</em> the peer block, never the incoming instructions or tools — this hook is
    /// contractually additive (see <c>AIContextProviderMergeContractTests</c>): the base merge appends
    /// whatever this returns onto the input, so echoing the input back here would duplicate it.
    /// </remarks>
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var peers = PeerAgentContextFormatter.GetPeers(_agentRegistry, _owningAgentId);
        var block = PeerAgentContextFormatter.FormatBlock(peers);

        return ValueTask.FromResult(block is null ? new AIContext() : new AIContext { Instructions = block });
    }
}
