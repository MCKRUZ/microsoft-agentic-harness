using Application.AI.Common.CQRS.Changes.CancelChangeProposal;
using Application.AI.Common.Tests.CQRS.Changes.Support;
using Domain.AI.Changes;
using Domain.Common;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.CQRS.Changes;

/// <summary>Handler tests for <see cref="CancelChangeProposalCommandHandler"/>.</summary>
public sealed class CancelChangeProposalCommandHandlerTests
{
    private static CancelChangeProposalCommandHandler NewSut(InMemoryChangeProposalStore store) =>
        new(store, TimeProvider.System);

    [Theory]
    [InlineData(ChangeProposalStatus.Draft)]
    [InlineData(ChangeProposalStatus.Validating)]
    [InlineData(ChangeProposalStatus.AwaitingApproval)]
    [InlineData(ChangeProposalStatus.Approved)]
    public async Task Handle_NonMergingNonTerminal_TransitionsToCancelled(ChangeProposalStatus status)
    {
        var store = new InMemoryChangeProposalStore();
        var proposal = TestHelpers.NewProposal(status);
        await store.SaveAsync(proposal, CancellationToken.None);
        var sut = NewSut(store);

        var result = await sut.Handle(
            new CancelChangeProposalCommand
            {
                ProposalId = proposal.Id,
                CancelledBy = "agent-self",
                Reason = "superseded by newer proposal"
            },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(ChangeProposalStatus.Cancelled);
        result.Value.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_Merging_ReturnsFailure()
    {
        var store = new InMemoryChangeProposalStore();
        var merging = TestHelpers.NewProposal(ChangeProposalStatus.Merging);
        await store.SaveAsync(merging, CancellationToken.None);
        var sut = NewSut(store);

        var result = await sut.Handle(
            new CancelChangeProposalCommand { ProposalId = merging.Id, CancelledBy = "x" },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("merge is in progress");
    }

    [Theory]
    [InlineData(ChangeProposalStatus.Merged)]
    [InlineData(ChangeProposalStatus.Rejected)]
    [InlineData(ChangeProposalStatus.Cancelled)]
    public async Task Handle_TerminalStatus_ReturnsFailure(ChangeProposalStatus status)
    {
        var store = new InMemoryChangeProposalStore();
        var terminal = TestHelpers.NewProposal(status);
        await store.SaveAsync(terminal, CancellationToken.None);
        var sut = NewSut(store);

        var result = await sut.Handle(
            new CancelChangeProposalCommand { ProposalId = terminal.Id, CancelledBy = "x" },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_UnknownProposal_ReturnsNotFound()
    {
        var store = new InMemoryChangeProposalStore();
        var sut = NewSut(store);

        var result = await sut.Handle(
            new CancelChangeProposalCommand { ProposalId = "missing", CancelledBy = "x" },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.NotFound);
    }
}
