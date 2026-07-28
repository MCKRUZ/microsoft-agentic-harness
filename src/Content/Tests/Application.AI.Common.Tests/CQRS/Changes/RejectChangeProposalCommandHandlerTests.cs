using Application.AI.Common.CQRS.Changes.RejectChangeProposal;
using Application.AI.Common.Interfaces.Changes;
using Application.AI.Common.Tests.CQRS.Changes.Support;
using Domain.AI.Changes;
using Domain.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Application.AI.Common.Tests.CQRS.Changes;

/// <summary>Handler tests for <see cref="RejectChangeProposalCommandHandler"/>.</summary>
public sealed class RejectChangeProposalCommandHandlerTests
{
    private static RejectChangeProposalCommandHandler NewSut(
        InMemoryChangeProposalStore store,
        IChangeAuditWriter? audit = null) =>
        new(
            store,
            audit ?? new TestHelpers.RecordingAuditWriter(),
            TestHelpers.EnabledConfigMonitor(),
            NullLogger<RejectChangeProposalCommandHandler>.Instance,
            TimeProvider.System);

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
    public async Task Handle_Rejected_AppendsReviewerIdToDurableAudit()
    {
        // Rejection is terminal and the orchestrator early-returns on terminal proposals, so this
        // handler's append is the ONLY chance this decision has to reach the durable chain.
        var store = new InMemoryChangeProposalStore();
        var pending = TestHelpers.NewProposal(ChangeProposalStatus.AwaitingApproval);
        await store.SaveAsync(pending, CancellationToken.None);
        var audit = new TestHelpers.RecordingAuditWriter();
        var sut = NewSut(store, audit);

        var result = await sut.Handle(
            new RejectChangeProposalCommand
            {
                ProposalId = pending.Id,
                ReviewerId = "carol@contoso.com",
                Reason = "no rollback plan"
            },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var entry = audit.Entries.Should().ContainSingle().Subject;
        entry.Decision.ReviewerId.Should().Be("carol@contoso.com");
        entry.Decision.Action.Should().Be(GateAction.Fail);
        entry.Decision.Reason.Should().Be("no rollback plan");
        entry.StatusAtAppend.Should().Be(ChangeProposalStatus.AwaitingApproval,
            "audit-then-save: the append must precede the terminal transition");
    }

    [Fact]
    public async Task Handle_AuditAppendThrows_LeavesProposalAwaitingApprovalAndFails()
    {
        var store = new InMemoryChangeProposalStore();
        var pending = TestHelpers.NewProposal(ChangeProposalStatus.AwaitingApproval);
        await store.SaveAsync(pending, CancellationToken.None);
        var sut = NewSut(store, new TestHelpers.ThrowingAuditWriter());

        var result = await sut.Handle(
            new RejectChangeProposalCommand
            {
                ProposalId = pending.Id,
                ReviewerId = "carol@contoso.com",
                Reason = "no rollback plan"
            },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Be("change_proposal.audit_append_failed");
        (await store.GetAsync(pending.Id, CancellationToken.None))!
            .Status.Should().Be(ChangeProposalStatus.AwaitingApproval,
                "an un-auditable rejection must not drive the proposal terminal");
    }

    [Fact]
    public async Task Handle_PipelineDisabled_ReturnsForbidden()
    {
        // The kill switch is documented as fail-fast for ALL change-proposal CQRS commands, not
        // just Approve.
        var store = new InMemoryChangeProposalStore();
        var pending = TestHelpers.NewProposal(ChangeProposalStatus.AwaitingApproval);
        await store.SaveAsync(pending, CancellationToken.None);
        var sut = new RejectChangeProposalCommandHandler(
            store,
            new TestHelpers.RecordingAuditWriter(),
            TestHelpers.DisabledConfigMonitor(),
            NullLogger<RejectChangeProposalCommandHandler>.Instance,
            TimeProvider.System);

        var result = await sut.Handle(
            new RejectChangeProposalCommand
            {
                ProposalId = pending.Id,
                ReviewerId = "carol@contoso.com",
                Reason = "x"
            },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Forbidden);
        result.Errors.Should().ContainSingle()
            .Which.Should().NotContain("AppConfig",
                "the configuration key belongs in the logs, not the response body");
        (await store.GetAsync(pending.Id, CancellationToken.None))!
            .Status.Should().Be(ChangeProposalStatus.AwaitingApproval);
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
    public async Task Handle_DraftStatus_ReturnsConflict()
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
        result.FailureType.Should().Be(ResultFailureType.Conflict,
            "a status-machine guard rejection is a state conflict (HTTP 409), not an opaque general failure (500)");
    }
}
