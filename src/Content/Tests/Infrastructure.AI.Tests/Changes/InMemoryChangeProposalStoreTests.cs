using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using FluentAssertions;
using Infrastructure.AI.Changes;
using Infrastructure.AI.Tests.Changes.Support;
using Xunit;

namespace Infrastructure.AI.Tests.Changes;

public sealed class InMemoryChangeProposalStoreTests
{
    [Fact]
    public async Task SaveAndGet_RoundTrips()
    {
        var store = new InMemoryChangeProposalStore();
        var proposal = TestProposals.NewProposal();

        await store.SaveAsync(proposal, CancellationToken.None);
        var fetched = await store.GetAsync(proposal.Id, CancellationToken.None);

        fetched.Should().Be(proposal);
    }

    [Fact]
    public async Task Save_Idempotent_OverwritesByLastWrite()
    {
        var store = new InMemoryChangeProposalStore();
        var initial = TestProposals.NewProposal();
        await store.SaveAsync(initial, CancellationToken.None);
        var updated = initial with { Status = ChangeProposalStatus.Validating };

        await store.SaveAsync(updated, CancellationToken.None);
        var fetched = await store.GetAsync(initial.Id, CancellationToken.None);

        fetched!.Status.Should().Be(ChangeProposalStatus.Validating);
    }

    [Fact]
    public async Task Get_UnknownId_ReturnsNull()
    {
        var store = new InMemoryChangeProposalStore();
        var fetched = await store.GetAsync("missing", CancellationToken.None);
        fetched.Should().BeNull();
    }

    [Fact]
    public async Task List_FiltersByStatus()
    {
        var store = new InMemoryChangeProposalStore();
        var draft = TestProposals.NewProposal();
        var awaiting = draft with { Id = draft.Id + "-2", Status = ChangeProposalStatus.AwaitingApproval };
        await store.SaveAsync(draft, CancellationToken.None);
        await store.SaveAsync(awaiting, CancellationToken.None);

        var results = await store.ListAsync(
            new ChangeProposalQuery { Status = ChangeProposalStatus.AwaitingApproval },
            CancellationToken.None);

        results.Should().ContainSingle().Which.Status.Should().Be(ChangeProposalStatus.AwaitingApproval);
    }

    [Fact]
    public async Task List_RespectsMaxResults()
    {
        var store = new InMemoryChangeProposalStore();
        for (var i = 0; i < 5; i++)
        {
            var p = TestProposals.NewProposal() with { Id = $"id-{i}" };
            await store.SaveAsync(p, CancellationToken.None);
        }

        var results = await store.ListAsync(
            new ChangeProposalQuery { MaxResults = 2 },
            CancellationToken.None);

        results.Should().HaveCount(2);
    }
}
