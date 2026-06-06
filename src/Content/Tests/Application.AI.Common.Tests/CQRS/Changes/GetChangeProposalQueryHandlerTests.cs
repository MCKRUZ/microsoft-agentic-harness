using Application.AI.Common.CQRS.Changes.GetChangeProposal;
using Application.AI.Common.Tests.CQRS.Changes.Support;
using Domain.Common;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.CQRS.Changes;

/// <summary>Handler tests for <see cref="GetChangeProposalQueryHandler"/>.</summary>
public sealed class GetChangeProposalQueryHandlerTests
{
    [Fact]
    public async Task Handle_KnownProposal_ReturnsIt()
    {
        var store = new InMemoryChangeProposalStore();
        var proposal = TestHelpers.NewProposal();
        await store.SaveAsync(proposal, CancellationToken.None);
        var sut = new GetChangeProposalQueryHandler(store);

        var result = await sut.Handle(new GetChangeProposalQuery { Id = proposal.Id }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Id.Should().Be(proposal.Id);
    }

    [Fact]
    public async Task Handle_UnknownProposal_ReturnsNotFound()
    {
        var store = new InMemoryChangeProposalStore();
        var sut = new GetChangeProposalQueryHandler(store);

        var result = await sut.Handle(new GetChangeProposalQuery { Id = "missing" }, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.NotFound);
    }
}
