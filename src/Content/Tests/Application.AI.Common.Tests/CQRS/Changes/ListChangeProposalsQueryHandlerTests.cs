using Application.AI.Common.CQRS.Changes.ListChangeProposals;
using Application.AI.Common.Interfaces.Changes;
using Application.AI.Common.Tests.CQRS.Changes.Support;
using Domain.AI.Changes;
using Domain.AI.Identity;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.CQRS.Changes;

/// <summary>Handler tests for <see cref="ListChangeProposalsQueryHandler"/>.</summary>
public sealed class ListChangeProposalsQueryHandlerTests
{
    [Fact]
    public async Task Handle_NoFilter_ReturnsAllProposals()
    {
        var store = new InMemoryChangeProposalStore();
        await SeedThreeProposals(store);
        var sut = new ListChangeProposalsQueryHandler(store);

        var result = await sut.Handle(
            new ListChangeProposalsQuery { Filter = new ChangeProposalQuery() },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(3);
    }

    [Fact]
    public async Task Handle_FilterByStatus_ReturnsMatching()
    {
        var store = new InMemoryChangeProposalStore();
        await SeedThreeProposals(store);
        var sut = new ListChangeProposalsQueryHandler(store);

        var result = await sut.Handle(
            new ListChangeProposalsQuery
            {
                Filter = new ChangeProposalQuery { Status = ChangeProposalStatus.AwaitingApproval }
            },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle()
            .Which.Status.Should().Be(ChangeProposalStatus.AwaitingApproval);
    }

    [Fact]
    public async Task Handle_FilterByAgent_ReturnsMatching()
    {
        var store = new InMemoryChangeProposalStore();
        await SeedThreeProposals(store);
        var sut = new ListChangeProposalsQueryHandler(store);

        var result = await sut.Handle(
            new ListChangeProposalsQuery
            {
                Filter = new ChangeProposalQuery { SubmittedByAgentId = "agent-other" }
            },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle()
            .Which.SubmittedBy.Id.Should().Be("agent-other");
    }

    [Fact]
    public async Task Handle_MaxResults_CapsResultCount()
    {
        var store = new InMemoryChangeProposalStore();
        await SeedThreeProposals(store);
        var sut = new ListChangeProposalsQueryHandler(store);

        var result = await sut.Handle(
            new ListChangeProposalsQuery { Filter = new ChangeProposalQuery { MaxResults = 2 } },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
    }

    private static async Task SeedThreeProposals(InMemoryChangeProposalStore store)
    {
        var p1 = TestHelpers.NewProposal();
        var p2 = TestHelpers.NewProposal(ChangeProposalStatus.AwaitingApproval);
        var otherIdentity = new AgentIdentity { Id = "agent-other", Kind = AgentIdentityKind.ManagedIdentity };
        var p3 = TestHelpers.NewProposal(identity: otherIdentity);
        // Ensure unique ids — p1 and p2 are derived from the same inputs so we mutate
        // one after creation. p3 differs by identity so it derives a different id.
        var p2Renamed = p2 with { Id = p2.Id + "-pending" };
        await store.SaveAsync(p1, CancellationToken.None);
        await store.SaveAsync(p2Renamed, CancellationToken.None);
        await store.SaveAsync(p3, CancellationToken.None);
    }
}
