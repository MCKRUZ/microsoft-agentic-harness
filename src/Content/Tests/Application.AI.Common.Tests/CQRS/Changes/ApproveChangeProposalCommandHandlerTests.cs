using Application.AI.Common.CQRS.Changes.ApproveChangeProposal;
using Application.AI.Common.Tests.CQRS.Changes.Support;
using Domain.AI.Changes;
using Domain.Common;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.CQRS.Changes;

/// <summary>Handler tests for <see cref="ApproveChangeProposalCommandHandler"/>.</summary>
public sealed class ApproveChangeProposalCommandHandlerTests
{
    private static ApproveChangeProposalCommandHandler NewSut(InMemoryChangeProposalStore store) =>
        new(store, new TestHelpers.StubOrchestrator(store), TestHelpers.EnabledConfigMonitor(), TimeProvider.System);

    [Fact]
    public async Task Handle_AwaitingApproval_TransitionsToApprovedAndPersists()
    {
        var store = new InMemoryChangeProposalStore();
        var pending = TestHelpers.NewProposal(ChangeProposalStatus.AwaitingApproval);
        await store.SaveAsync(pending, CancellationToken.None);
        var sut = NewSut(store);

        var result = await sut.Handle(
            new ApproveChangeProposalCommand
            {
                ProposalId = pending.Id,
                ReviewerId = "user-42",
                Reason = "approved via portal"
            },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(ChangeProposalStatus.Approved);
        result.Value.History.Should().ContainSingle();
        result.Value.History[0].GateKey.Should().Be("approval");
        result.Value.History[0].Action.Should().Be(GateAction.Pass);
        result.Value.History[0].ReviewerId.Should().Be("user-42");
        result.Value.History[0].Reason.Should().Be("approved via portal");

        (await store.GetAsync(pending.Id, CancellationToken.None))!
            .Status.Should().Be(ChangeProposalStatus.Approved);
    }

    [Fact]
    public async Task Handle_UnknownProposal_ReturnsNotFound()
    {
        var store = new InMemoryChangeProposalStore();
        var sut = NewSut(store);

        var result = await sut.Handle(
            new ApproveChangeProposalCommand { ProposalId = "missing", ReviewerId = "user-42" },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.NotFound);
    }

    [Theory]
    [InlineData(ChangeProposalStatus.Draft)]
    [InlineData(ChangeProposalStatus.Validating)]
    [InlineData(ChangeProposalStatus.Approved)]
    [InlineData(ChangeProposalStatus.Merging)]
    [InlineData(ChangeProposalStatus.Merged)]
    [InlineData(ChangeProposalStatus.Rejected)]
    [InlineData(ChangeProposalStatus.Cancelled)]
    public async Task Handle_WrongStatus_ReturnsFailure(ChangeProposalStatus status)
    {
        var store = new InMemoryChangeProposalStore();
        var proposal = TestHelpers.NewProposal(status);
        await store.SaveAsync(proposal, CancellationToken.None);
        var sut = NewSut(store);

        var result = await sut.Handle(
            new ApproveChangeProposalCommand { ProposalId = proposal.Id, ReviewerId = "user-42" },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_OrchestratorReturnsNull_ReturnsNotFoundInsteadOfStaleApproved()
    {
        // Race: orchestrator returns null when the store lost the proposal
        // between Approve's Save and the orchestrator's Get. Don't return the
        // stale Approved snapshot — surface as NotFound.
        var store = new InMemoryChangeProposalStore();
        var pending = TestHelpers.NewProposal(ChangeProposalStatus.AwaitingApproval);
        await store.SaveAsync(pending, CancellationToken.None);
        var orchestrator = new TestHelpers.StubOrchestrator(storeForPassThrough: null);
        var sut = new ApproveChangeProposalCommandHandler(
            store, orchestrator, TestHelpers.EnabledConfigMonitor(), TimeProvider.System);

        var result = await sut.Handle(
            new ApproveChangeProposalCommand { ProposalId = pending.Id, ReviewerId = "user-42" },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.NotFound);
        result.Errors.Should().ContainSingle().Which.Should().Contain("deleted before");
        orchestrator.InvocationCount.Should().Be(1);
    }
}
