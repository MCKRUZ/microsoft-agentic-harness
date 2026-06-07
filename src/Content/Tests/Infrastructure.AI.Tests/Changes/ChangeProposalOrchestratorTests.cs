using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using Domain.AI.Identity;
using FluentAssertions;
using Infrastructure.AI.Changes;
using Infrastructure.AI.Tests.Changes.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using GateAction = Domain.AI.Changes.GateAction;

namespace Infrastructure.AI.Tests.Changes;

/// <summary>
/// End-to-end tests for <see cref="ChangeProposalOrchestrator"/>. Exercises:
/// happy-path lifecycle, validation failure, defer-resume, shadow mode no-effect,
/// gate exception → Rejected, idempotent on terminal, auto-approve when no
/// approval gate, missing gate registration → Rejected.
/// </summary>
public sealed class ChangeProposalOrchestratorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly InMemoryChangeProposalStore _store;
    private readonly JsonlChangeAuditWriter _audit;

    public ChangeProposalOrchestratorTests()
    {
        var (monitor, dir) = TestConfig.NewMonitor();
        _tempDir = dir;
        _store = new InMemoryChangeProposalStore();
        _audit = new JsonlChangeAuditWriter(monitor, NullLogger<JsonlChangeAuditWriter>.Instance);
        _monitor = monitor;
    }

    private readonly Microsoft.Extensions.Options.IOptionsMonitor<Domain.Common.Config.AppConfig> _monitor;

    public void Dispose()
    {
        _audit.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private ChangeProposalOrchestrator BuildSut(params IChangeProposalGate[] gates)
    {
        var services = new ServiceCollection();
        foreach (var gate in gates)
        {
            services.AddKeyedSingleton(gate.Key, gate);
        }
        return new ChangeProposalOrchestrator(
            _store,
            _audit,
            services.BuildServiceProvider(),
            TimeProvider.System,
            _monitor,
            NullLogger<ChangeProposalOrchestrator>.Instance);
    }

    [Fact]
    public async Task ProcessAsync_DraftWithValidationPassAndApprovalDefers_TransitionsToAwaitingApproval()
    {
        var proposal = TestProposals.NewProposal();
        await _store.SaveAsync(proposal, CancellationToken.None);

        // Merge gate registered but never reached — approval defers before
        // the merge phase runs. Required because phase-based partition reads
        // each gate's declared Phase via DI; an unregistered key defaults to
        // Validation, which would silently misclassify "merge" if omitted.
        var sut = BuildSut(
            new TestProposals.StubGate(WellKnownGateKeys.SelfValidation, GateResult.Pass()),
            new TestProposals.StubGate(WellKnownGateKeys.Approval, GateResult.Defer("queued for human", TimeSpan.FromMinutes(5))),
            new TestProposals.StubGate(WellKnownGateKeys.Merge, GateResult.Pass()));

        var result = await sut.ProcessAsync(proposal.Id, OrchestratorMode.Live, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Status.Should().Be(ChangeProposalStatus.AwaitingApproval);
    }

    [Fact]
    public async Task ProcessAsync_ApprovalGatePass_TransitionsThroughApprovedToMerged()
    {
        var proposal = TestProposals.NewProposal();
        await _store.SaveAsync(proposal, CancellationToken.None);

        var sut = BuildSut(
            new TestProposals.StubGate(WellKnownGateKeys.SelfValidation, GateResult.Pass()),
            new TestProposals.StubGate(WellKnownGateKeys.Approval, GateResult.Pass("autonomy-tier auto-approve")),
            new TestProposals.StubGate(WellKnownGateKeys.Merge, GateResult.Pass("applied")));

        var result = await sut.ProcessAsync(proposal.Id, OrchestratorMode.Live, CancellationToken.None);

        result!.Status.Should().Be(ChangeProposalStatus.Merged);
        result.History.Should().Contain(h =>
            h.GateKey == WellKnownGateKeys.Approval && h.Action == GateAction.Pass);
    }

    [Fact]
    public async Task ProcessAsync_ApprovalGateFail_TransitionsToRejected()
    {
        var proposal = TestProposals.NewProposal();
        await _store.SaveAsync(proposal, CancellationToken.None);

        // Merge gate registered but never reached — approval Fail terminates
        // the pipeline before the merge phase. Required for the same partition
        // reason as the approval-defer test above.
        var sut = BuildSut(
            new TestProposals.StubGate(WellKnownGateKeys.SelfValidation, GateResult.Pass()),
            new TestProposals.StubGate(WellKnownGateKeys.Approval, GateResult.Fail("blocked by policy")),
            new TestProposals.StubGate(WellKnownGateKeys.Merge, GateResult.Pass()));

        var result = await sut.ProcessAsync(proposal.Id, OrchestratorMode.Live, CancellationToken.None);

        result!.Status.Should().Be(ChangeProposalStatus.Rejected);
        // Assert the rejection came from the approval gate, not from a
        // misclassified missing-merge-gate failure.
        result.History.Last().GateKey.Should().Be(WellKnownGateKeys.Approval);
        result.History.Last().Reason.Should().Contain("blocked by policy");
    }

    [Fact]
    public async Task ProcessAsync_NoApprovalGateRegisteredButRequired_TransitionsToRejected()
    {
        var proposal = TestProposals.NewProposal();
        await _store.SaveAsync(proposal, CancellationToken.None);

        // SelfValidation registered, but no "approval" gate even though it's in RequiredGates.
        var sut = BuildSut(new TestProposals.StubGate(WellKnownGateKeys.SelfValidation, GateResult.Pass()));

        var result = await sut.ProcessAsync(proposal.Id, OrchestratorMode.Live, CancellationToken.None);

        result!.Status.Should().Be(ChangeProposalStatus.Rejected);
        result.History.Last().Reason.Should().Contain("approval");
    }

    [Fact]
    public async Task ProcessAsync_ApprovedWithMergePass_TransitionsToMerged()
    {
        var proposal = TestProposals.NewProposal() with { Status = ChangeProposalStatus.Approved };
        await _store.SaveAsync(proposal, CancellationToken.None);

        var sut = BuildSut(new TestProposals.StubGate(WellKnownGateKeys.Merge, GateResult.Pass("applied")));

        var result = await sut.ProcessAsync(proposal.Id, OrchestratorMode.Live, CancellationToken.None);

        result!.Status.Should().Be(ChangeProposalStatus.Merged);
    }

    [Fact]
    public async Task ProcessAsync_ValidationFail_TransitionsToRejected()
    {
        var proposal = TestProposals.NewProposal();
        await _store.SaveAsync(proposal, CancellationToken.None);

        var failingGate = new TestProposals.StubGate(WellKnownGateKeys.SelfValidation, GateResult.Fail("missing tests"));
        var mergeGate = new TestProposals.StubGate(WellKnownGateKeys.Merge, GateResult.Pass()); // should not be reached
        var sut = BuildSut(failingGate, mergeGate);

        var result = await sut.ProcessAsync(proposal.Id, OrchestratorMode.Live, CancellationToken.None);

        result!.Status.Should().Be(ChangeProposalStatus.Rejected);
        mergeGate.InvocationCount.Should().Be(0);
    }

    [Fact]
    public async Task ProcessAsync_GateThrows_TransitionsToRejected()
    {
        var proposal = TestProposals.NewProposal();
        await _store.SaveAsync(proposal, CancellationToken.None);

        var sut = BuildSut(new TestProposals.ThrowingGate(WellKnownGateKeys.SelfValidation));

        var result = await sut.ProcessAsync(proposal.Id, OrchestratorMode.Live, CancellationToken.None);

        result!.Status.Should().Be(ChangeProposalStatus.Rejected);
        result.History.Last().Reason.Should().Contain("InvalidOperationException");
    }

    [Fact]
    public async Task ProcessAsync_MissingGateRegistration_TransitionsToRejected()
    {
        var proposal = TestProposals.NewProposal();
        await _store.SaveAsync(proposal, CancellationToken.None);

        // No SelfValidation gate registered.
        var sut = BuildSut();

        var result = await sut.ProcessAsync(proposal.Id, OrchestratorMode.Live, CancellationToken.None);

        result!.Status.Should().Be(ChangeProposalStatus.Rejected);
        result.History.Last().Reason.Should().Contain("No IChangeProposalGate registered");
    }

    [Fact]
    public async Task ProcessAsync_TrivialBlastRadiusNoApprovalGate_AutoApprovesToMerged()
    {
        var proposal = TestProposals.NewProposal(
            gates: new[] { WellKnownGateKeys.SelfValidation, WellKnownGateKeys.Merge },
            blastRadius: BlastRadius.Trivial);
        await _store.SaveAsync(proposal, CancellationToken.None);

        var sut = BuildSut(
            new TestProposals.StubGate(WellKnownGateKeys.SelfValidation, GateResult.Pass()),
            new TestProposals.StubGate(WellKnownGateKeys.Merge, GateResult.Pass("applied")));

        var result = await sut.ProcessAsync(proposal.Id, OrchestratorMode.Live, CancellationToken.None);

        result!.Status.Should().Be(ChangeProposalStatus.Merged);
        // Auto-approve records an explicit synthetic approval entry so an
        // auditor can distinguish it from a real human nod.
        var approvalEntry = result.History.Single(h =>
            h.GateKey == WellKnownGateKeys.Approval && h.Action == GateAction.Pass);
        approvalEntry.ReviewerId.Should().Be("auto-approver");
        approvalEntry.Reason.Should().Contain("auto-approved");
    }

    [Fact]
    public async Task ProcessAsync_ShadowMode_DoesNotPreventTransitionToMerged()
    {
        // Shadow mode still drives the proposal through the state machine to
        // Merged so the lifecycle is visible; the merge gate (which would do
        // the real mutation) is responsible for honoring Mode == Shadow.
        var proposal = TestProposals.NewProposal(
            gates: new[] { WellKnownGateKeys.SelfValidation, WellKnownGateKeys.Merge },
            blastRadius: BlastRadius.Trivial);
        await _store.SaveAsync(proposal, CancellationToken.None);

        var sut = BuildSut(
            new TestProposals.StubGate(WellKnownGateKeys.SelfValidation, GateResult.Pass()),
            new TestProposals.StubGate(WellKnownGateKeys.Merge, GateResult.Pass("shadow")));

        var result = await sut.ProcessAsync(proposal.Id, OrchestratorMode.Shadow, CancellationToken.None);

        result!.Status.Should().Be(ChangeProposalStatus.Merged);
    }

    [Fact]
    public async Task ProcessAsync_GateDefers_StaysAtValidating()
    {
        var proposal = TestProposals.NewProposal();
        await _store.SaveAsync(proposal, CancellationToken.None);

        var deferring = new TestProposals.StubGate(
            WellKnownGateKeys.SelfValidation,
            GateResult.Defer("upstream not ready", TimeSpan.FromSeconds(30)));
        var sut = BuildSut(deferring);

        var result = await sut.ProcessAsync(proposal.Id, OrchestratorMode.Live, CancellationToken.None);

        result!.Status.Should().Be(ChangeProposalStatus.Validating);
        result.History.Last().Action.Should().Be(GateAction.Defer);
    }

    [Fact]
    public async Task ProcessAsync_DeferBudgetExhausted_TransitionsToRejected()
    {
        var proposal = TestProposals.NewProposal();
        await _store.SaveAsync(proposal, CancellationToken.None);

        var deferring = new TestProposals.StubGate(
            WellKnownGateKeys.SelfValidation,
            GateResult.Defer("upstream not ready", TimeSpan.FromSeconds(1)));
        var sut = BuildSut(deferring);

        // Call repeatedly until the orchestrator's defer budget (3 per test config) is exhausted.
        ChangeProposal? current = null;
        for (var i = 0; i < 5; i++)
        {
            current = await sut.ProcessAsync(proposal.Id, OrchestratorMode.Live, CancellationToken.None);
            if (current!.IsTerminal) break;
        }

        current!.Status.Should().Be(ChangeProposalStatus.Rejected);
        current.History.Last().Reason.Should().Contain("defer budget exhausted");
    }

    [Fact]
    public async Task ProcessAsync_TerminalProposal_IsIdempotent()
    {
        var merged = TestProposals.NewProposal() with { Status = ChangeProposalStatus.Merged };
        await _store.SaveAsync(merged, CancellationToken.None);
        var sut = BuildSut();

        var result = await sut.ProcessAsync(merged.Id, OrchestratorMode.Live, CancellationToken.None);

        result!.Status.Should().Be(ChangeProposalStatus.Merged);
        result.History.Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessAsync_AwaitingApproval_DoesNotAdvance()
    {
        var pending = TestProposals.NewProposal() with { Status = ChangeProposalStatus.AwaitingApproval };
        await _store.SaveAsync(pending, CancellationToken.None);
        var sut = BuildSut(new TestProposals.StubGate(WellKnownGateKeys.Merge, GateResult.Pass()));

        var result = await sut.ProcessAsync(pending.Id, OrchestratorMode.Live, CancellationToken.None);

        result!.Status.Should().Be(ChangeProposalStatus.AwaitingApproval);
    }

    [Fact]
    public async Task ProcessAsync_UnknownProposal_ReturnsNull()
    {
        var sut = BuildSut();
        var result = await sut.ProcessAsync("missing", OrchestratorMode.Live, CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task ProcessAsync_WritesAuditLineForEveryGateAndTransition()
    {
        var proposal = TestProposals.NewProposal(
            gates: new[] { WellKnownGateKeys.SelfValidation, WellKnownGateKeys.Merge },
            blastRadius: BlastRadius.Trivial);
        await _store.SaveAsync(proposal, CancellationToken.None);
        var sut = BuildSut(
            new TestProposals.StubGate(WellKnownGateKeys.SelfValidation, GateResult.Pass()),
            new TestProposals.StubGate(WellKnownGateKeys.Merge, GateResult.Pass("applied")));

        await sut.ProcessAsync(proposal.Id, OrchestratorMode.Live, CancellationToken.None);

        var auditPath = Path.Combine(_tempDir, "audit", "changes.jsonl");
        var lines = await File.ReadAllLinesAsync(auditPath);
        // Expected entries: Draft→Validating, self_validation Pass, validation phase done,
        // Approved→Merging, merge Pass, merge phase done. That's 6.
        lines.Length.Should().BeGreaterThanOrEqualTo(5);
    }
}
