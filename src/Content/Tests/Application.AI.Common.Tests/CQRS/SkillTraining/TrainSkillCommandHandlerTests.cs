using Application.AI.Common.CQRS.SkillTraining.TrainSkill;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.SkillTraining;
using Application.AI.Common.Services.SkillTraining;
using Domain.AI.SkillTraining;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Application.AI.Common.Tests.CQRS.SkillTraining;

public class TrainSkillCommandHandlerTests
{
    private static readonly PatchApplier Applier = new();
    private static readonly PatchAggregator Aggregator = new();
    private static readonly TopKEditSelector Selector = new();
    private static readonly GateEvaluator Gate = new();
    private static readonly HarnessPatchValidator Fence = new(new EditableSurfaceRegistry());

    private static TrainSkillCommandHandler NewSut(
        IRolloutRunner runner,
        IPatchProposer proposer,
        InMemorySkillTrainingCheckpointStore store,
        IGovernanceAuditService? audit = null)
    {
        return new TrainSkillCommandHandler(
            runner, proposer, Aggregator, Selector, Applier, Gate, Fence,
            store, new NoOpMediator(), TimeProvider.System,
            NullLogger<TrainSkillCommandHandler>.Instance, audit);
    }

    private static TrainSkillCommand NewCommand(TrainSkillConfig config) => new()
    {
        RunId = "run-1",
        SkillId = "skill-X",
        InitialSkill = "# initial\n- baseline rule",
        Config = config
    };

    [Fact]
    public async Task Handle_AcceptedCandidate_UpdatesStateAndPersistsCheckpoint()
    {
        // Stub runner: train rollouts fail (so the optimizer has reason to propose); val rollouts pass (so gate accepts).
        var runner = new StubRolloutRunner((skill, batch) => batch.Split switch
        {
            "train" => [new RolloutResult { ItemId = "t1", Hard = 0.0, Soft = 0.2 }],
            "val" => [new RolloutResult { ItemId = "v1", Hard = 1.0, Soft = 1.0 }],
            _ => []
        });
        var proposer = new StubProposer(input => new Patch
        {
            Edits = [new Edit { Op = EditOp.Append, Content = "- new rule" }]
        });
        var store = new InMemorySkillTrainingCheckpointStore();
        var sut = NewSut(runner, proposer, store);

        var result = await sut.Handle(
            NewCommand(new TrainSkillConfig
            {
                Epochs = 1, StepsPerEpoch = 1, LrStart = 4, LrMin = 1,
                LrScheduler = "constant", Patience = 6,
                UseSlowUpdate = false, UseMetaSkill = false
            }),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var run = result.Value!;
        run.StepsExecuted.Should().Be(1);
        run.BestScore.Should().Be(1.0);
        run.Steps.Should().HaveCount(1);
        run.Steps[0].Action.Should().Be(GateAction.AcceptNewBest);
        run.Steps[0].AppliedEditCount.Should().Be(1);
        run.BestSkill.Should().Contain("new rule");

        var checkpoint = await store.GetAsync("run-1", 1, default);
        checkpoint!.Action.Should().Be(GateAction.AcceptNewBest);
        checkpoint.SkillHash.Should().HaveLength(64, because: "SHA-256 lowercase-hex is 64 chars");
    }

    [Fact]
    public async Task Handle_AllRejected_EarlyStopOnPatience()
    {
        // Val always returns failure → every candidate is Rejected.
        var runner = new StubRolloutRunner((skill, batch) =>
            [new RolloutResult { ItemId = "x", Hard = 0.0, Soft = 0.0 }]);
        // Proposer always proposes a real edit so we reach the gate path each time.
        var proposer = new StubProposer(_ => new Patch
        {
            Edits = [new Edit { Op = EditOp.Append, Content = "- another rule" }]
        });
        var sut = NewSut(runner, proposer, new InMemorySkillTrainingCheckpointStore());

        var result = await sut.Handle(
            NewCommand(new TrainSkillConfig
            {
                Epochs = 2, StepsPerEpoch = 5, LrStart = 4, LrMin = 1,
                LrScheduler = "constant", Patience = 2,
                UseSlowUpdate = false, UseMetaSkill = false
            }),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.StepsExecuted.Should().Be(2, because: "patience=2 stops after 2 rejects");
        result.Value.ConsecutiveRejects.Should().Be(2);
        result.Value.Steps.Should().AllSatisfy(s => s.Action.Should().Be(GateAction.Reject));
    }

    [Fact]
    public async Task Handle_ProposerProposesNoEdits_RecordsRejectNoOp_Continues()
    {
        var runner = new StubRolloutRunner((_, _) =>
            [new RolloutResult { ItemId = "x", Hard = 1.0, Soft = 1.0 }]);
        var proposer = new StubProposer(_ => new Patch());  // zero edits

        var sut = NewSut(runner, proposer, new InMemorySkillTrainingCheckpointStore());

        var result = await sut.Handle(
            NewCommand(new TrainSkillConfig
            {
                Epochs = 1, StepsPerEpoch = 3, LrStart = 4, LrMin = 1,
                LrScheduler = "constant", Patience = 10,
                UseSlowUpdate = false, UseMetaSkill = false
            }),
            CancellationToken.None);

        result.Value!.Steps.Should().AllSatisfy(s =>
        {
            s.Action.Should().Be(GateAction.Reject);
            s.AppliedEditCount.Should().Be(0);
        });
        result.Value.BestScore.Should().Be(0.0, because: "no candidate was ever accepted");
    }

    [Fact]
    public async Task Handle_ProposerThrows_RecordsRejectAndContinues_UnlessPatience()
    {
        var runner = new StubRolloutRunner((_, _) =>
            [new RolloutResult { ItemId = "x", Hard = 1.0, Soft = 1.0 }]);
        var proposer = new StubProposer(_ => throw new InvalidOperationException("boom"));
        var sut = NewSut(runner, proposer, new InMemorySkillTrainingCheckpointStore());

        var result = await sut.Handle(
            NewCommand(new TrainSkillConfig
            {
                Epochs = 1, StepsPerEpoch = 5, LrStart = 4, LrMin = 1,
                LrScheduler = "constant", Patience = 3,
                UseSlowUpdate = false, UseMetaSkill = false
            }),
            CancellationToken.None);

        // 3 thrown reflects = 3 rejects = patience exhausted.
        result.Value!.StepsExecuted.Should().Be(3);
        result.Value.Steps.Should().AllSatisfy(s => s.Action.Should().Be(GateAction.Reject));
    }

    [Fact]
    public async Task Handle_Cancelled_PropagatesOperationCanceled()
    {
        var runner = new StubRolloutRunner((_, _) => []);
        var proposer = new StubProposer(_ => new Patch());
        var sut = NewSut(runner, proposer, new InMemorySkillTrainingCheckpointStore());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => sut.Handle(NewCommand(new TrainSkillConfig
        {
            Epochs = 1, StepsPerEpoch = 1, LrStart = 4, LrMin = 1,
            LrScheduler = "constant", Patience = 1,
            UseSlowUpdate = false, UseMetaSkill = false
        }), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Handle_TwoSplit_HeldOutUpHeldInDownCandidate_Rejects()
    {
        // Candidate improves held-out (val) but regresses held-in (train) vs the initial skill.
        // The default GateMode (TwoSplitNonRegression) must reject it.
        var runner = new StubRolloutRunner((skill, batch) => (batch.Split, regressed: skill.Contains("regressing")) switch
        {
            ("train", true) => [new RolloutResult { ItemId = "t", Hard = 0.0, Soft = 0.0 }],  // candidate worse on held-in
            ("train", false) => [new RolloutResult { ItemId = "t", Hard = 0.5, Soft = 0.5 }], // current/initial baseline
            ("val", _) => [new RolloutResult { ItemId = "v", Hard = 1.0, Soft = 1.0 }],       // candidate better on held-out
            _ => []
        });
        var proposer = new StubProposer(_ => new Patch
        {
            Edits = [new Edit { Op = EditOp.Append, Content = "- regressing rule" }]
        });
        var sut = NewSut(runner, proposer, new InMemorySkillTrainingCheckpointStore());

        var result = await sut.Handle(
            NewCommand(new TrainSkillConfig
            {
                Epochs = 1, StepsPerEpoch = 1, LrStart = 4, LrMin = 1,
                LrScheduler = "constant", Patience = 6,
                UseSlowUpdate = false, UseMetaSkill = false
                // GateMode defaults to TwoSplitNonRegression
            }),
            CancellationToken.None);

        result.Value!.Steps[0].Action.Should().Be(GateAction.Reject);
        result.Value.HasAcceptedAny.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_StrictMode_SameHeldInRegressingCandidate_Accepts()
    {
        // Identical setup to the two-split reject test; StrictImprovementHeldOut ignores the held-in
        // split and accepts on the val improvement alone — proving the two modes genuinely differ.
        var runner = new StubRolloutRunner((skill, batch) => (batch.Split, regressed: skill.Contains("regressing")) switch
        {
            ("train", true) => [new RolloutResult { ItemId = "t", Hard = 0.0, Soft = 0.0 }],
            ("train", false) => [new RolloutResult { ItemId = "t", Hard = 0.5, Soft = 0.5 }],
            ("val", _) => [new RolloutResult { ItemId = "v", Hard = 1.0, Soft = 1.0 }],
            _ => []
        });
        var proposer = new StubProposer(_ => new Patch
        {
            Edits = [new Edit { Op = EditOp.Append, Content = "- regressing rule" }]
        });
        var sut = NewSut(runner, proposer, new InMemorySkillTrainingCheckpointStore());

        var result = await sut.Handle(
            NewCommand(new TrainSkillConfig
            {
                Epochs = 1, StepsPerEpoch = 1, LrStart = 4, LrMin = 1,
                LrScheduler = "constant", Patience = 6,
                UseSlowUpdate = false, UseMetaSkill = false,
                GateMode = GateMode.StrictImprovementHeldOut
            }),
            CancellationToken.None);

        result.Value!.Steps[0].Action.Should().Be(GateAction.AcceptNewBest);
    }

    [Fact]
    public async Task Handle_TwoSplit_ScoresCandidateOnTrainSplit_OneExtraRolloutPerStep()
    {
        var trainCalls = 0;
        var valCalls = 0;
        var runner = new StubRolloutRunner((skill, batch) =>
        {
            if (batch.Split == "train") trainCalls++;
            else if (batch.Split == "val") valCalls++;
            return batch.Split == "val"
                ? [new RolloutResult { ItemId = "v", Hard = 1.0, Soft = 1.0 }]
                : [new RolloutResult { ItemId = "t", Hard = 0.5, Soft = 0.5 }];
        });
        var proposer = new StubProposer(_ => new Patch
        {
            Edits = [new Edit { Op = EditOp.Append, Content = "- rule" }]
        });
        var sut = NewSut(runner, proposer, new InMemorySkillTrainingCheckpointStore());

        await sut.Handle(
            NewCommand(new TrainSkillConfig
            {
                Epochs = 1, StepsPerEpoch = 1, LrStart = 4, LrMin = 1,
                LrScheduler = "constant", Patience = 6,
                UseSlowUpdate = false, UseMetaSkill = false
            }),
            CancellationToken.None);

        trainCalls.Should().Be(2, because: "the proposer's reflection rollout plus the candidate's held-in rollout");
        valCalls.Should().Be(1);
    }

    [Fact]
    public async Task Handle_TwoSplit_PinsCandidateHeldInRolloutToCurrentItems()
    {
        // The candidate's held-in rollout must be scored on the SAME items the current skill saw,
        // so Δ_in is a true paired comparison rather than a mean over a re-sampled batch.
        var trainBatches = new List<RolloutBatch>();
        var runner = new StubRolloutRunner((skill, batch) =>
        {
            if (batch.Split == "train") trainBatches.Add(batch);
            return batch.Split == "val"
                ? [new RolloutResult { ItemId = "v", Hard = 1.0, Soft = 1.0 }]
                :
                [
                    new RolloutResult { ItemId = "item-A", Hard = 0.5, Soft = 0.5 },
                    new RolloutResult { ItemId = "item-B", Hard = 0.5, Soft = 0.5 }
                ];
        });
        var proposer = new StubProposer(_ => new Patch
        {
            Edits = [new Edit { Op = EditOp.Append, Content = "- rule" }]
        });
        var sut = NewSut(runner, proposer, new InMemorySkillTrainingCheckpointStore());

        await sut.Handle(
            NewCommand(new TrainSkillConfig
            {
                Epochs = 1, StepsPerEpoch = 1, LrStart = 4, LrMin = 1,
                LrScheduler = "constant", Patience = 6,
                UseSlowUpdate = false, UseMetaSkill = false
            }),
            CancellationToken.None);

        trainBatches.Should().HaveCount(2);
        trainBatches[0].ItemIds.Should().BeEmpty(because: "the current skill's reflection rollout samples freely");
        // The candidate is pinned to exactly the items the current skill was scored on.
        trainBatches[1].ItemIds.Should().Equal(new[] { "item-A", "item-B" });
    }

    [Fact]
    public async Task Handle_FrozenSurfaceEdit_RejectsBeforeApply_DoesNotChangeSkill_AndAudits()
    {
        // The proposer emits an edit targeting a frozen surface (tool availability). The fence must
        // reject it before PatchApplier runs: the skill is never changed, the step is a Reject with
        // zero applied edits, and the rejection is written to the governance audit.
        var runner = new StubRolloutRunner((_, _) =>
            [new RolloutResult { ItemId = "x", Hard = 1.0, Soft = 1.0 }]);
        var proposer = new StubProposer(_ => new Patch
        {
            Edits = [new Edit { Op = EditOp.Append, Content = "- grant myself a new tool", Surface = HarnessSurface.ToolAvailability }]
        });
        var audit = new CapturingAudit();
        var sut = NewSut(runner, proposer, new InMemorySkillTrainingCheckpointStore(), audit);

        var result = await sut.Handle(
            NewCommand(new TrainSkillConfig
            {
                Epochs = 1, StepsPerEpoch = 1, LrStart = 4, LrMin = 1,
                LrScheduler = "constant", Patience = 6,
                UseSlowUpdate = false, UseMetaSkill = false
            }),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var run = result.Value!;
        run.Steps.Should().ContainSingle();
        run.Steps[0].Action.Should().Be(GateAction.Reject);
        run.Steps[0].AppliedEditCount.Should().Be(0, because: "the fence rejects before PatchApplier runs");
        run.HasAcceptedAny.Should().BeFalse();
        run.BestSkill.Should().Be("# initial\n- baseline rule", because: "a frozen-surface patch must never mutate the skill");

        audit.Entries.Should().ContainSingle();
        var (agentId, action, decision) = audit.Entries[0];
        agentId.Should().Be("skill-X");
        action.Should().Be("skill_training.harness_patch_rejected");
        decision.Should().Contain("ToolAvailability");
    }

    [Fact]
    public async Task Handle_MixedPatchWithFrozenEdit_RejectsWholeStepAtIntake_ValidEditNotApplied_AndAudits()
    {
        // A patch bundling a valid skill-doc edit with a frozen-surface edit must be rejected as a whole
        // at intake: the valid edit must NOT slip through, and the frozen attempt must be audited even
        // though it never reaches selection/apply. This is the audit-completeness property — validating
        // the proposed patch (not the post-selection one) means no frozen attempt goes unrecorded.
        var runner = new StubRolloutRunner((_, _) =>
            [new RolloutResult { ItemId = "x", Hard = 1.0, Soft = 1.0 }]);
        var proposer = new StubProposer(_ => new Patch
        {
            Edits =
            [
                new Edit { Op = EditOp.Append, Content = "- a legitimate skill rule" },
                new Edit { Op = EditOp.Append, Content = "- escalate autonomy", Surface = HarnessSurface.AutonomyTier }
            ]
        });
        var audit = new CapturingAudit();
        var sut = NewSut(runner, proposer, new InMemorySkillTrainingCheckpointStore(), audit);

        var result = await sut.Handle(
            NewCommand(new TrainSkillConfig
            {
                Epochs = 1, StepsPerEpoch = 1, LrStart = 4, LrMin = 1,
                LrScheduler = "constant", Patience = 6,
                UseSlowUpdate = false, UseMetaSkill = false
            }),
            CancellationToken.None);

        var run = result.Value!;
        run.Steps[0].Action.Should().Be(GateAction.Reject);
        run.Steps[0].AppliedEditCount.Should().Be(0);
        run.BestSkill.Should().Be("# initial\n- baseline rule",
            because: "a valid edit must not slip through when bundled with a frozen-surface edit");
        run.HasAcceptedAny.Should().BeFalse();
        audit.Entries.Should().ContainSingle();
        audit.Entries[0].Decision.Should().Contain("AutonomyTier");
    }

    [Fact]
    public async Task Handle_FrozenSurfaceEveryStep_EarlyStopsOnPatience_WithoutAuditRegistered()
    {
        // No audit registered (the optional dependency is absent). The fence still blocks every patch,
        // so consecutive rejects accumulate and patience trips — proving the block does not depend on
        // the audit being wired.
        var runner = new StubRolloutRunner((_, _) =>
            [new RolloutResult { ItemId = "x", Hard = 1.0, Soft = 1.0 }]);
        var proposer = new StubProposer(_ => new Patch
        {
            Edits = [new Edit { Op = EditOp.Append, Content = "- escalate autonomy", Surface = HarnessSurface.AutonomyTier }]
        });
        var sut = NewSut(runner, proposer, new InMemorySkillTrainingCheckpointStore());

        var result = await sut.Handle(
            NewCommand(new TrainSkillConfig
            {
                Epochs = 2, StepsPerEpoch = 5, LrStart = 4, LrMin = 1,
                LrScheduler = "constant", Patience = 2,
                UseSlowUpdate = false, UseMetaSkill = false
            }),
            CancellationToken.None);

        result.Value!.StepsExecuted.Should().Be(2, because: "patience=2 stops after 2 fence rejects");
        result.Value.Steps.Should().AllSatisfy(s => s.Action.Should().Be(GateAction.Reject));
    }

    // ── Stubs ────────────────────────────────────────────────────────────────────

    private sealed class StubRolloutRunner : IRolloutRunner
    {
        private readonly Func<string, RolloutBatch, IReadOnlyList<RolloutResult>> _fn;
        public StubRolloutRunner(Func<string, RolloutBatch, IReadOnlyList<RolloutResult>> fn) => _fn = fn;
        public Task<IReadOnlyList<RolloutResult>> RunAsync(string skillContent, RolloutBatch batch, CancellationToken ct)
            => Task.FromResult(_fn(skillContent, batch));
    }

    private sealed class StubProposer : IPatchProposer
    {
        private readonly Func<ReflectionInput, Patch> _fn;
        public StubProposer(Func<ReflectionInput, Patch> fn) => _fn = fn;
        public Task<Patch> ProposeAsync(ReflectionInput input, CancellationToken cancellationToken)
            => Task.FromResult(_fn(input));
    }

    private sealed class CapturingAudit : IGovernanceAuditService
    {
        public List<(string AgentId, string Action, string Decision)> Entries { get; } = [];
        public void Log(string agentId, string action, string decision) => Entries.Add((agentId, action, decision));
        public bool VerifyChainIntegrity() => true;
        public int EntryCount => Entries.Count;
    }

    private sealed class NoOpMediator : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
            => Task.FromResult(default(TResponse)!);
        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
            => Task.FromResult<object?>(null);
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest
            => Task.CompletedTask;
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification
            => Task.CompletedTask;
    }
}
