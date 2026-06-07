using Application.AI.Common.Interfaces.Changes;
using Domain.AI.Changes;
using FluentAssertions;
using Infrastructure.AI.Changes;
using Infrastructure.AI.Tests.Changes.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using GateAction = Domain.AI.Changes.GateAction;

namespace Infrastructure.AI.Tests.Changes;

/// <summary>
/// Regression tests for PR-2 follow-ups (6) + (8): the orchestrator partitions
/// <c>RequiredGates</c> into validation / approval / merge phases by querying
/// each registered gate's declared <see cref="GatePhase"/>, not by string-matching
/// on the hardcoded <c>"approval"</c> key. These tests pin the behaviour that
/// the old take-while/skip-while heuristic got wrong.
/// </summary>
public sealed class ChangeProposalOrchestratorPhasePartitionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly InMemoryChangeProposalStore _store;
    private readonly JsonlChangeAuditWriter _audit;
    private readonly Microsoft.Extensions.Options.IOptionsMonitor<Domain.Common.Config.AppConfig> _monitor;

    public ChangeProposalOrchestratorPhasePartitionTests()
    {
        var (monitor, dir) = TestConfig.NewMonitor();
        _tempDir = dir;
        _monitor = monitor;
        _store = new InMemoryChangeProposalStore();
        _audit = new JsonlChangeAuditWriter(monitor, NullLogger<JsonlChangeAuditWriter>.Instance);
    }

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
    public async Task ProcessAsync_CustomValidationGateWithoutApproval_RunsEachGateInCorrectPhaseExactlyOnce()
    {
        // Item (8) regression: previously
        //   RequiredGates = ["self_validation", "compliance", "merge"] without approval
        // produced:
        //   - ValidationGates take-while-not-approval → [self_validation, compliance, merge]
        //     (merge mis-runs in validation phase)
        //   - MergeGates fallback → [compliance, merge]
        //     (compliance mis-runs in merge phase too)
        // Now: gates declare their phase; partition is correct.
        var proposal = TestProposals.NewProposal(
            gates: new[]
            {
                WellKnownGateKeys.SelfValidation,
                "compliance",
                WellKnownGateKeys.Merge
            },
            blastRadius: BlastRadius.Trivial); // auto-approve path
        await _store.SaveAsync(proposal, CancellationToken.None);

        var selfVal = new TestProposals.StubGate(WellKnownGateKeys.SelfValidation, GateResult.Pass());
        var compliance = new TestProposals.StubGate("compliance", GateResult.Pass(), GatePhase.Validation);
        var merge = new TestProposals.StubGate(WellKnownGateKeys.Merge, GateResult.Pass());

        var sut = BuildSut(selfVal, compliance, merge);

        var result = await sut.ProcessAsync(proposal.Id, OrchestratorMode.Live, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Status.Should().Be(ChangeProposalStatus.Merged);

        // Each gate invoked exactly once — neither compliance nor merge double-runs.
        selfVal.InvocationCount.Should().Be(1);
        compliance.InvocationCount.Should().Be(1);
        merge.InvocationCount.Should().Be(1);
    }

    [Fact]
    public async Task ProcessAsync_CustomApprovalGateKey_IsRecognizedByPhaseNotByLiteralKey()
    {
        // A consumer that wires a "quorum_approval" gate with Phase.Approval
        // should be routed through AwaitingApproval on Defer. The orchestrator
        // must not depend on the literal string "approval" for that decision.
        var proposal = TestProposals.NewProposal(
            gates: new[]
            {
                WellKnownGateKeys.SelfValidation,
                "quorum_approval",
                WellKnownGateKeys.Merge
            });
        await _store.SaveAsync(proposal, CancellationToken.None);

        var sut = BuildSut(
            new TestProposals.StubGate(WellKnownGateKeys.SelfValidation, GateResult.Pass()),
            new TestProposals.StubGate(
                "quorum_approval",
                GateResult.Defer("queued for quorum", TimeSpan.FromMinutes(5)),
                GatePhase.Approval),
            new TestProposals.StubGate(WellKnownGateKeys.Merge, GateResult.Pass()));

        var result = await sut.ProcessAsync(proposal.Id, OrchestratorMode.Live, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Status.Should().Be(ChangeProposalStatus.AwaitingApproval);
        result.History.Should().Contain(h =>
            h.GateKey == "quorum_approval" && h.Action == GateAction.Defer);
    }

    [Fact]
    public async Task ProcessAsync_RequiredGatesNotInPhaseOrder_StillPartitionsByPhase()
    {
        // RequiredGates ordered merge → validation → approval. The orchestrator
        // must still run validation first, approval second, merge last. Within
        // a phase, order from RequiredGates is preserved (only matters when
        // multiple gates share a phase).
        var proposal = TestProposals.NewProposal(
            gates: new[]
            {
                WellKnownGateKeys.Merge,           // merge phase, listed first
                WellKnownGateKeys.SelfValidation,  // validation phase
                WellKnownGateKeys.Approval         // approval phase, listed last
            });
        await _store.SaveAsync(proposal, CancellationToken.None);

        var orderLog = new List<string>();
        var selfVal = new RecordingGate(WellKnownGateKeys.SelfValidation, GatePhase.Validation, orderLog);
        var approval = new RecordingGate(WellKnownGateKeys.Approval, GatePhase.Approval, orderLog);
        var merge = new RecordingGate(WellKnownGateKeys.Merge, GatePhase.Merge, orderLog);

        var sut = BuildSut(selfVal, approval, merge);

        var result = await sut.ProcessAsync(proposal.Id, OrchestratorMode.Live, CancellationToken.None);

        result!.Status.Should().Be(ChangeProposalStatus.Merged);
        orderLog.Should().Equal("self_validation", "approval", "merge");
    }

    private sealed class RecordingGate(string key, GatePhase phase, List<string> log) : IChangeProposalGate
    {
        public string Key { get; } = key;
        public GatePhase Phase { get; } = phase;
        public Task<GateResult> EvaluateAsync(ChangeProposal proposal, GateContext context, CancellationToken cancellationToken)
        {
            log.Add(Key);
            return Task.FromResult(GateResult.Pass());
        }
    }
}
