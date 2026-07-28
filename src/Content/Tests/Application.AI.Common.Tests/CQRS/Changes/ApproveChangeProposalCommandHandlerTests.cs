using Application.AI.Common.CQRS.Changes.ApproveChangeProposal;
using Application.AI.Common.Interfaces.Changes;
using Application.AI.Common.Tests.CQRS.Changes.Support;
using Domain.AI.Changes;
using Domain.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Application.AI.Common.Tests.CQRS.Changes;

/// <summary>Handler tests for <see cref="ApproveChangeProposalCommandHandler"/>.</summary>
public sealed class ApproveChangeProposalCommandHandlerTests
{
    private static ApproveChangeProposalCommandHandler NewSut(
        InMemoryChangeProposalStore store,
        TestHelpers.StubDispatchQueue? dispatcher = null,
        IChangeAuditWriter? audit = null) =>
        new(
            store,
            dispatcher ?? new TestHelpers.StubDispatchQueue(),
            audit ?? new TestHelpers.RecordingAuditWriter(),
            TestHelpers.EnabledConfigMonitor(),
            NullLogger<ApproveChangeProposalCommandHandler>.Instance,
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
    public async Task Handle_Approved_AppendsReviewerIdToDurableAuditBeforeSaving()
    {
        // The proposal store's production default is in-process and dies with the host, so the
        // durable chain is the only lasting record of who approved. It must carry the
        // token-derived reviewer id, and it must be written BEFORE the state change so a
        // persisted approval can never exist without its audit line.
        var store = new InMemoryChangeProposalStore();
        var pending = TestHelpers.NewProposal(ChangeProposalStatus.AwaitingApproval);
        await store.SaveAsync(pending, CancellationToken.None);
        var audit = new TestHelpers.RecordingAuditWriter();
        var sut = NewSut(store, audit: audit);

        var result = await sut.Handle(
            new ApproveChangeProposalCommand
            {
                ProposalId = pending.Id,
                ReviewerId = "alice@contoso.com",
                Reason = "reviewed the diff"
            },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var entry = audit.Entries.Should().ContainSingle().Subject;
        entry.ProposalId.Should().Be(pending.Id);
        entry.Decision.ReviewerId.Should().Be("alice@contoso.com",
            "the durable audit must name the reviewer the token identified");
        entry.Decision.GateKey.Should().Be("approval");
        entry.Decision.Action.Should().Be(GateAction.Pass);
        entry.Decision.Reason.Should().Be("reviewed the diff");
        entry.CorrelationId.Should().NotBeNullOrWhiteSpace(
            "the audit line must be stitchable to the surrounding logs/traces");
        entry.StatusAtAppend.Should().Be(ChangeProposalStatus.AwaitingApproval,
            "audit-then-save: the append must happen before the transition is persisted");
    }

    [Fact]
    public async Task Handle_AuditAppendThrows_LeavesProposalAwaitingApprovalAndFailsWithoutLeakingDetail()
    {
        // Fail closed. If the decision cannot be recorded it must not take effect — otherwise a
        // merge proceeds that nobody can attribute.
        var store = new InMemoryChangeProposalStore();
        var pending = TestHelpers.NewProposal(ChangeProposalStatus.AwaitingApproval);
        await store.SaveAsync(pending, CancellationToken.None);
        var dispatcher = new TestHelpers.StubDispatchQueue();
        var audit = new TestHelpers.ThrowingAuditWriter();
        var sut = NewSut(store, dispatcher, audit);

        var result = await sut.Handle(
            new ApproveChangeProposalCommand { ProposalId = pending.Id, ReviewerId = "alice@contoso.com" },
            CancellationToken.None);

        audit.Attempts.Should().Be(1);
        result.IsSuccess.Should().BeFalse();
        (await store.GetAsync(pending.Id, CancellationToken.None))!
            .Status.Should().Be(ChangeProposalStatus.AwaitingApproval,
                "an un-auditable decision must not advance the state machine");
        dispatcher.Enqueued.Should().BeEmpty(
            "no merge may be scheduled for a decision that was never recorded");
        result.Errors.Should().ContainSingle()
            .Which.Should().Be("change_proposal.audit_append_failed")
            .And.NotContain("secret-audit-path",
                "the audit sink's exception text (which embeds storage paths) must stay in the logs");
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
            new TestHelpers.RecordingAuditWriter(),
            TestHelpers.DisabledConfigMonitor(),
            NullLogger<ApproveChangeProposalCommandHandler>.Instance,
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
