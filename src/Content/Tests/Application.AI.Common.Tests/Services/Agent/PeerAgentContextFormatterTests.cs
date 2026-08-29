using Application.AI.Common.Interfaces;
using Application.AI.Common.Services.Agent;
using Domain.AI.Agents;
using FluentAssertions;
using Moq;

namespace Application.AI.Common.Tests.Services.Agent;

/// <summary>
/// Regression coverage for the #518 correctness-review defect: a null <c>owningAgentId</c> used to
/// mean "exclude nothing," so an agent built with no owning identity (an orchestrator built via
/// <c>SkillAgentOptions</c> with no <c>OwningAgentId</c>, or the parameterless
/// <c>CreateAgentFromSkillAsync(skillId)</c> overload) got the entire agent registry injected into its
/// instructions while the context bar's <c>Agents</c> lane — which relies on the same null check via
/// <c>ExecuteAgentTurnCommandHandler.BuildRegistrationSnapshot</c> — charged zero for it. The
/// provider-level case (nothing actually injected) is covered in
/// <c>AIContextProviderMergeContractTests.PeerAgentContext_NoOwningAgentId_InjectsNothing</c>.
/// </summary>
public sealed class PeerAgentContextFormatterTests
{
    private static Mock<IAgentMetadataRegistry> BuildRegistry(params AgentDefinition[] agents)
    {
        var registry = new Mock<IAgentMetadataRegistry>();
        registry.Setup(r => r.GetAll()).Returns(agents);
        return registry;
    }

    private static AgentDefinition Agent(string id) =>
        new() { Id = id, Name = id, Description = $"{id} description" };

    [Fact]
    public void GetPeers_NullOwningAgentId_ReturnsNoPeers()
    {
        var registry = BuildRegistry(Agent("peer-a"), Agent("peer-b"));

        var peers = PeerAgentContextFormatter.GetPeers(registry.Object, owningAgentId: null);

        peers.Should().BeEmpty();
    }

    [Fact]
    public void GetPeers_KnownOwningAgentId_ReturnsEveryOtherRegisteredAgent()
    {
        var registry = BuildRegistry(Agent("self"), Agent("peer-a"), Agent("peer-b"));

        var peers = PeerAgentContextFormatter.GetPeers(registry.Object, owningAgentId: "self");

        peers.Select(p => p.Id).Should().BeEquivalentTo("peer-a", "peer-b");
    }
}
