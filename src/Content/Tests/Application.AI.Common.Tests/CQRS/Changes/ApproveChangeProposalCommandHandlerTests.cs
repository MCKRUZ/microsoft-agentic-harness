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
    private static ApproveChangeProposalCommandHandler NewSut(
        InMemoryChangeProposalStore store,
        TestHelpers.StubDispatchQueue? dispatcher = null) =>
        new(
            store,
            dispatcher ?? new TestHelpers.StubDispatchQueue(),
            TestHelpers.EnabledConfigMonitor(),
            TimeProvider.System);

    [Fact]
    public async Task Handle_AwaitingApproval_TransitionsToApprovedPersistsAndEnqueues()
    {
        var store = new InMemoryChangeProposalStore();
        var pending = TestHelpers.NewProposal(ChangeProposalStatus.AwaitingApproval);
        await store.SaveAsync(pending, CancellationToken.None);
        var dispatcher = new TestHelpers.StubDispatchQueue();
        var sut = NewSut(store, dispatcher);

        var result = await sut.Handle(
            new ApproveChangeProposalCommand
            {
                ProposalId = pending.Id,
                ReviewerId = "user-42",
                Reason = "approved via portal"
            },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        // Status is Approved — handler transitions then enqueues; the
        // merge phase runs out-of-band in the BackgroundService.
        result.Value!.Status.Should().Be(ChangeProposalStatus.Approved);
        result.Value.History.Should().ContainSingle();
        result.Value.History[0].GateKey.Should().Be("approval");
        result.Value.History[0].Action.Should().Be(GateAction.Pass);
        result.Value.History[0].ReviewerId.Should().Be("user-42");
        result.Value.History[0].Reason.Should().Be("approved via portal");

        (await store.GetAsync(pending.Id, CancellationToken.None))!
            .Status.Should().Be(ChangeProposalStatus.Approved);

        // Side-effect guard: the proposal was queued for merge-phase dispatch.
        dispatcher.Enqueued.Should().ContainSingle().Which.Should().Be(pending.Id);
    }

    [Fact]
    public async Task Handle_UnknownProposal_ReturnsNotFoundAndDoesNotEnqueue()
    {
        var store = new InMemoryChangeProposalStore();
        var dispatcher = new TestHelpers.StubDispatchQueue();
        var sut = NewSut(store, dispatcher);

        var result = await sut.Handle(
            new ApproveChangeProposalCommand { ProposalId = "missing", ReviewerId = "user-42" },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.NotFound);
        dispatcher.Enqueued.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ChangeProposalStatus.Draft)]
    [InlineData(ChangeProposalStatus.Validating)]
    [InlineData(ChangeProposalStatus.Approved)]
    [InlineData(ChangeProposalStatus.Merging)]
    [InlineData(ChangeProposalStatus.Merged)]
    [InlineData(ChangeProposalStatus.Rejected)]
    [InlineData(ChangeProposalStatus.Cancelled)]
    public async Task Handle_WrongStatus_ReturnsConflictAndDoesNotEnqueue(ChangeProposalStatus status)
    {
        var store = new InMemoryChangeProposalStore();
        var proposal = TestHelpers.NewProposal(status);
        await store.SaveAsync(proposal, CancellationToken.None);
        var dispatcher = new TestHelpers.StubDispatchQueue();
        var sut = NewSut(store, dispatcher);

        var result = await sut.Handle(
            new ApproveChangeProposalCommand { ProposalId = proposal.Id, ReviewerId = "user-42" },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Conflict,
            "a status-machine guard rejection is a state conflict (HTTP 409), not an opaque general failure (500)");
        dispatcher.Enqueued.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_PipelineDisabled_ReturnsForbiddenAndDoesNotEnqueue()
    {
        var store = new InMemoryChangeProposalStore();
        var pending = TestHelpers.NewProposal(ChangeProposalStatus.AwaitingApproval);
        await store.SaveAsync(pending, CancellationToken.None);
        var dispatcher = new TestHelpers.StubDispatchQueue();
        var sut = new ApproveChangeProposalCommandHandler(
            store,
            dispatcher,
            TestHelpers.DisabledConfigMonitor(),
            TimeProvider.System);

        var result = await sut.Handle(
            new ApproveChangeProposalCommand { ProposalId = pending.Id, ReviewerId = "user-42" },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Forbidden);
        result.Errors.Should().ContainSingle().Which.Should().Contain("disabled");
        dispatcher.Enqueued.Should().BeEmpty();
        (await store.GetAsync(pending.Id, CancellationToken.None))!
            .Status.Should().Be(ChangeProposalStatus.AwaitingApproval);
    }
}
