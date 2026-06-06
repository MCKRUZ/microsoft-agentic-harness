using Application.AI.Common.CQRS.Changes.RejectChangeProposal;
using Application.AI.Common.Tests.CQRS.Changes.Support;
using Domain.AI.Changes;
using Domain.Common;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.CQRS.Changes;

/// <summary>Handler tests for <see cref="RejectChangeProposalCommandHandler"/>.</summary>
public sealed class RejectChangeProposalCommandHandlerTests
{
    private static RejectChangeProposalCommandHandler NewSut(InMemoryChangeProposalStore store) =>
        new(store, TimeProvider.System);

    [Fact]
    public async Task Handle_AwaitingApproval_TransitionsToRejectedAndCapturesReason()
    {
        var store = new InMemoryChangeProposalStore();
        var pending = TestHelpers.NewProposal(ChangeProposalStatus.AwaitingApproval);
        await store.SaveAsync(pending, CancellationToken.None);
        var sut = NewSut(store);

        var result = await sut.Handle(
            new RejectChangeProposalCommand
            {
                ProposalId = pending.Id,
                ReviewerId = "user-99",
                Reason = "production change requires SOC2 ticket"
            },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(ChangeProposalStatus.Rejected);
        result.Value.IsTerminal.Should().BeTrue();
        result.Value.History.Should().ContainSingle()
            .Which.Reason.Should().Be("production change requires SOC2 ticket");
    }

    [Fact]
    public async Task Handle_UnknownProposal_ReturnsNotFound()
    {
        var store = new InMemoryChangeProposalStore();
        var sut = NewSut(store);

        var result = await sut.Handle(
            new RejectChangeProposalCommand
            {
                ProposalId = "missing",
                ReviewerId = "user-99",
                Reason = "doesn't matter"
            },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.NotFound);
    }

    [Fact]
    public async Task Handle_DraftStatus_ReturnsFailure()
    {
        var store = new InMemoryChangeProposalStore();
        var draft = TestHelpers.NewProposal(ChangeProposalStatus.Draft);
        await store.SaveAsync(draft, CancellationToken.None);
        var sut = NewSut(store);

        var result = await sut.Handle(
            new RejectChangeProposalCommand
            {
                ProposalId = draft.Id,
                ReviewerId = "user-99",
                Reason = "x"
            },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
    }
}
