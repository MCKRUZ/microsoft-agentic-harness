using Application.AI.Common.CQRS.Changes.CancelChangeProposal;
using Application.AI.Common.Interfaces.Changes;
using Application.AI.Common.Tests.CQRS.Changes.Support;
using Domain.AI.Changes;
using Domain.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Application.AI.Common.Tests.CQRS.Changes;

/// <summary>Handler tests for <see cref="CancelChangeProposalCommandHandler"/>.</summary>
public sealed class CancelChangeProposalCommandHandlerTests
{
    private static CancelChangeProposalCommandHandler NewSut(
        InMemoryChangeProposalStore store,
        IChangeAuditWriter? audit = null) =>
        new(
            store,
            audit ?? new TestHelpers.RecordingAuditWriter(),
            TestHelpers.EnabledConfigMonitor(),
            NullLogger<CancelChangeProposalCommandHandler>.Instance,
            TimeProvider.System);

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
    public async Task Handle_Merging_ReturnsConflict()
    {
        var store = new InMemoryChangeProposalStore();
        var merging = TestHelpers.NewProposal(ChangeProposalStatus.Merging);
        await store.SaveAsync(merging, CancellationToken.None);
        var sut = NewSut(store);

        var result = await sut.Handle(
            new CancelChangeProposalCommand { ProposalId = merging.Id, CancelledBy = "x" },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Conflict,
            "cancel-during-merge is a state conflict (HTTP 409), not an opaque general failure (500)");
        result.Errors.Should().ContainSingle().Which.Should().Contain("merge is in progress");
    }

    [Theory]
    [InlineData(ChangeProposalStatus.Merged)]
    [InlineData(ChangeProposalStatus.Rejected)]
    [InlineData(ChangeProposalStatus.Cancelled)]
    public async Task Handle_TerminalStatus_ReturnsConflict(ChangeProposalStatus status)
    {
        var store = new InMemoryChangeProposalStore();
        var terminal = TestHelpers.NewProposal(status);
        await store.SaveAsync(terminal, CancellationToken.None);
        var sut = NewSut(store);

        var result = await sut.Handle(
            new CancelChangeProposalCommand { ProposalId = terminal.Id, CancelledBy = "x" },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Conflict,
            "cancelling a terminal proposal is a state conflict (HTTP 409), not an opaque general failure (500)");
    }

    [Fact]
    public async Task Handle_Cancelled_AppendsCancellingIdentityToDurableAudit()
    {
        // Cancellation is terminal and the orchestrator early-returns on terminal proposals, so
        // this handler's append is the ONLY chance this decision has to reach the durable chain.
        var store = new InMemoryChangeProposalStore();
        var pending = TestHelpers.NewProposal(ChangeProposalStatus.AwaitingApproval);
        await store.SaveAsync(pending, CancellationToken.None);
        var audit = new TestHelpers.RecordingAuditWriter();
        var sut = NewSut(store, audit);

        var result = await sut.Handle(
            new CancelChangeProposalCommand
            {
                ProposalId = pending.Id,
                CancelledBy = "ops@contoso.com",
                Reason = "superseded"
            },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var entry = audit.Entries.Should().ContainSingle().Subject;
        entry.Decision.ReviewerId.Should().Be("ops@contoso.com");
        entry.Decision.GateKey.Should().Be(CancelChangeProposalCommandHandler.CancellationGateKey);
        entry.StatusAtAppend.Should().Be(ChangeProposalStatus.AwaitingApproval,
            "audit-then-save: the append must precede the terminal transition");
    }

    [Fact]
    public async Task Handle_AuditAppendThrows_LeavesProposalUnchangedAndFails()
    {
        var store = new InMemoryChangeProposalStore();
        var pending = TestHelpers.NewProposal(ChangeProposalStatus.AwaitingApproval);
        await store.SaveAsync(pending, CancellationToken.None);
        var sut = NewSut(store, new TestHelpers.ThrowingAuditWriter());

        var result = await sut.Handle(
            new CancelChangeProposalCommand { ProposalId = pending.Id, CancelledBy = "ops@contoso.com" },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Be("change_proposal.audit_append_failed");
        (await store.GetAsync(pending.Id, CancellationToken.None))!
            .Status.Should().Be(ChangeProposalStatus.AwaitingApproval,
                "an un-auditable cancellation must not drive the proposal terminal");
    }

    [Fact]
    public async Task Handle_PipelineDisabled_ReturnsForbidden()
    {
        // The kill switch is documented as fail-fast for ALL change-proposal CQRS commands.
        var store = new InMemoryChangeProposalStore();
        var pending = TestHelpers.NewProposal(ChangeProposalStatus.AwaitingApproval);
        await store.SaveAsync(pending, CancellationToken.None);
        var sut = new CancelChangeProposalCommandHandler(
            store,
            new TestHelpers.RecordingAuditWriter(),
            TestHelpers.DisabledConfigMonitor(),
            NullLogger<CancelChangeProposalCommandHandler>.Instance,
            TimeProvider.System);

        var result = await sut.Handle(
            new CancelChangeProposalCommand { ProposalId = pending.Id, CancelledBy = "ops@contoso.com" },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Forbidden);
        (await store.GetAsync(pending.Id, CancellationToken.None))!
            .Status.Should().Be(ChangeProposalStatus.AwaitingApproval);
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
