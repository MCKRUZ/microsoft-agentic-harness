using StubProposer = Tests.Common.SkillTraining.ScriptedPatchProposer;
using StubRolloutRunner = Tests.Common.SkillTraining.ScriptedRolloutRunner;
using Application.AI.Common.Interfaces.SkillTraining;
using Application.AI.Common.Services.SkillTraining;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.SkillTraining;
using Domain.Common;
using FluentAssertions;

namespace Application.AI.Common.Tests.CQRS.SkillTraining;

/// <summary>
/// The structured experience a training run carries (#845): what the gate showed about each tried edit,
/// how that reaches the epoch's lesson, what a fresh run starts with, and what a resume may restore.
/// </summary>
public partial class TrainSkillCommandHandlerTests
{
    private static ExperienceEntry Lesson(string text, string runId = "run-1", string skillId = "skill-X", string domain = "general", int epoch = 1) => new()
    {
        Cue = "when editing",
        Lesson = text,
        Confidence = 0.5,
        SkillHash = SkillContentHash.Of("s"),
        SkillContent = "s",
        Domain = domain,
        SkillId = skillId,
        RunId = runId,
        Epoch = epoch
    };

    private sealed class FakeExperienceLibrary : IExperienceLibrary
    {
        public IReadOnlyList<ExperienceEntry> Recalled { get; init; } = [];
        public Exception? RecallFails { get; init; }
        public List<(string Domain, string SkillId, string RunId, int BeforeEpoch)> RecallCalls { get; } = [];

        public Task<Result<MemoryWriteOutcome>> PersistAsync(ExperienceEntry entry, CancellationToken cancellationToken) =>
            Task.FromResult(Result<MemoryWriteOutcome>.Success(MemoryWriteOutcome.Persisted));

        public Task<IReadOnlyList<ExperienceEntry>> RecallAsync(
            string domain, string skillId, string runId, int beforeEpoch, int maxResults, CancellationToken cancellationToken)
        {
            RecallCalls.Add((domain, skillId, runId, beforeEpoch));
            return RecallFails is not null ? throw RecallFails : Task.FromResult(Recalled);
        }

        public Task ForgetAsync(string domain, string skillId, string runId, int epoch, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// A rollout runner whose validation score follows the script, one value per candidate evaluation:
    /// 1.0 beats the starting score of 0, so it is accepted; a lower or equal one is rejected.
    /// </summary>
    private static StubRolloutRunner ScriptedValidation(params double[] valScores)
    {
        var next = 0;
        return new StubRolloutRunner((s, b) => b.Split switch
        {
            "train" => [new RolloutResult { ItemId = "t1", Hard = 0.0, Soft = 0.0 }],
            "val" => [new RolloutResult { ItemId = "v1", Hard = valScores[Math.Min(next++, valScores.Length - 1)], Soft = 0.0 }],
            _ => []
        });
    }

    private static TrainSkillConfig HardGate(int steps, int epochs = 1, bool meta = false) => new()
    {
        Epochs = epochs, StepsPerEpoch = steps, LrStart = 4, LrMin = 1, LrScheduler = "constant", Patience = 10,
        UseSlowUpdate = false, UseMetaSkill = meta, GateMetric = GateMetric.Hard
    };

    private static async Task<IReadOnlyList<SkillTrainingCheckpoint>> RunAndListAsync(
        InMemorySkillTrainingCheckpointStore store, StubRolloutRunner runner, StubProposer proposer, TrainSkillConfig config)
    {
        await NewSut(runner, proposer, store).Handle(NewCommand(config), CancellationToken.None);
        return (await store.ListAsync("run-1", CancellationToken.None)).OrderBy(c => c.Step).ToList();
    }

    [Fact]
    public async Task Handle_AnAcceptedLoneEdit_IsATrialTheGateHelped()
    {
        var checkpoints = await RunAndListAsync(
            new InMemorySkillTrainingCheckpointStore(), ScriptedValidation(1.0), AppendingProposer(), HardGate(steps: 1));

        var trial = checkpoints.Single().EditTrials.Should().ContainSingle().Subject;
        trial.Verdict.Should().Be(EditVerdict.Helped);
        trial.Step.Should().Be(1);
        trial.Description.Should().Be("Append \"- a rule\"");
    }

    [Fact]
    public async Task Handle_ALoneEditThatScoredBelowTheSkillItStartedFrom_IsATrialThatHurt()
    {
        // Step 1 is accepted (current becomes 1.0); step 2's candidate scores 0.0, below it.
        var checkpoints = await RunAndListAsync(
            new InMemorySkillTrainingCheckpointStore(), ScriptedValidation(1.0, 0.0), AppendingProposer(), HardGate(steps: 2));

        checkpoints[1].EditTrials.Should().ContainSingle().Which.Verdict.Should().Be(EditVerdict.Hurt);
    }

    [Fact]
    public async Task Handle_ALoneEditThatScoredTheSameAsTheSkillItStartedFrom_IsATrialWithNoObservedEffect()
    {
        var checkpoints = await RunAndListAsync(
            new InMemorySkillTrainingCheckpointStore(), ScriptedValidation(1.0, 1.0), AppendingProposer(), HardGate(steps: 2));

        checkpoints[1].EditTrials.Should().ContainSingle().Which.Verdict.Should().Be(EditVerdict.NoObservedEffect);
    }

    [Fact]
    public async Task Handle_SeveralEditsInOneStep_AreEachConfoundedWhateverTheGateDecided()
    {
        var proposer = new StubProposer(_ => new Patch
        {
            Edits = [new Edit { Op = EditOp.Append, Content = "- one" }, new Edit { Op = EditOp.Append, Content = "- two" }]
        });

        var checkpoints = await RunAndListAsync(
            new InMemorySkillTrainingCheckpointStore(), ScriptedValidation(1.0), proposer, HardGate(steps: 1));

        checkpoints.Single().EditTrials.Should().HaveCount(2).And.OnlyContain(t => t.Verdict == EditVerdict.Confounded);
    }

    [Fact]
    public async Task Handle_AStepThatEndedBeforeTheGate_HasNoTrials()
    {
        var emptyProposer = new StubProposer(_ => new Patch { Edits = [] });

        var checkpoints = await RunAndListAsync(
            new InMemorySkillTrainingCheckpointStore(), ScriptedValidation(1.0), emptyProposer, HardGate(steps: 1));

        checkpoints.Single().EditTrials.Should().BeEmpty("nothing was tried, so there is nothing to learn from");
    }

    [Fact]
    public async Task Handle_TheEpochBoundary_GetsTheEpochsTrialsAndNoOthers()
    {
        var mediator = new MetaMemoryMediator("lesson");
        var config = HardGate(steps: 2, epochs: 2, meta: true);

        await NewSut(ScriptedValidation(1.0, 0.0, 1.0, 0.0), AppendingProposer(), new InMemorySkillTrainingCheckpointStore(), mediator: mediator)
            .Handle(NewCommand(config), CancellationToken.None);

        var update = mediator.Commands.Should().ContainSingle("the last epoch has no boundary").Subject;
        update.Epoch.Should().Be(1);
        update.Trials.Select(t => t.Step).Should().Equal(1, 2);
        update.Trials.Select(t => t.Verdict).Should().Equal(EditVerdict.Helped, EditVerdict.Hurt);
        update.Domain.Should().Be("general");
    }

    [Fact]
    public async Task Handle_ARunResumedMidEpoch_StillGivesTheBoundaryTheEpochsEarlierTrials()
    {
        var store = new InMemorySkillTrainingCheckpointStore();
        var config = HardGate(steps: 2, epochs: 2, meta: true);
        await NewSut(ScriptedValidation(1.0, 1.0), InterruptedDuringStep(2), store, mediator: new MetaMemoryMediator("lesson"))
            .Invoking(s => s.Handle(NewCommand(config), CancellationToken.None))
            .Should().ThrowAsync<OperationCanceledException>();

        var mediator = new MetaMemoryMediator("lesson");
        await NewSut(ScriptedValidation(1.0, 1.0), AppendingProposer(), store, mediator: mediator)
            .Handle(NewCommand(config), CancellationToken.None);

        mediator.Commands.Single().Trials.Select(t => t.Step).Should().Equal(
            new[] { 1, 2 }, "step 1's trial was run by the interrupted attempt and comes back from its checkpoint");
    }

    [Fact]
    public async Task Handle_AFreshRun_StartsWithTheLessonsEarlierRunsLeft()
    {
        var library = new FakeExperienceLibrary { Recalled = [Lesson("do not reword the heading", runId: "run-0")] };
        var seen = new List<string>();
        var proposer = new StubProposer(input =>
        {
            seen.Add(input.MetaSkillMemory);
            return new Patch { Edits = [new Edit { Op = EditOp.Append, Content = "- a rule" }] };
        });

        await NewSut(ScriptedValidation(1.0), proposer, new InMemorySkillTrainingCheckpointStore(), library: library)
            .Handle(NewCommand(HardGate(steps: 1, meta: true)), CancellationToken.None);

        library.RecallCalls.Should().ContainSingle().Which.Should().Be(("general", "skill-X", "run-1", 1));
        seen.Should().ContainSingle().Which.Should().Contain("do not reword the heading");
    }

    [Fact]
    public async Task Handle_WithTheMetaSkillUpdateOff_RecallsNothing()
    {
        var library = new FakeExperienceLibrary { Recalled = [Lesson("x")] };
        var seen = new List<string>();
        var proposer = new StubProposer(input =>
        {
            seen.Add(input.MetaSkillMemory);
            return new Patch { Edits = [new Edit { Op = EditOp.Append, Content = "- a rule" }] };
        });

        await NewSut(ScriptedValidation(1.0), proposer, new InMemorySkillTrainingCheckpointStore(), library: library)
            .Handle(NewCommand(HardGate(steps: 1, meta: false)), CancellationToken.None);

        library.RecallCalls.Should().BeEmpty();
        seen.Should().ContainSingle().Which.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_AMemoryThatCannotBeReached_GivesAnEmptyStartNotAFailedRun()
    {
        var library = new FakeExperienceLibrary { RecallFails = new InvalidOperationException("upstream said: SECRET") };

        var result = await NewSut(ScriptedValidation(1.0), AppendingProposer(), new InMemorySkillTrainingCheckpointStore(), library: library)
            .Handle(NewCommand(HardGate(steps: 1, meta: true)), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_AResumedRun_UsesItsCheckpointNotTheMemory()
    {
        var store = new InMemorySkillTrainingCheckpointStore();
        var config = HardGate(steps: 2, epochs: 2, meta: true);
        await NewSut(ScriptedValidation(1.0, 1.0), InterruptedDuringStep(2), store, mediator: new MetaMemoryMediator("lesson"))
            .Invoking(s => s.Handle(NewCommand(config), CancellationToken.None))
            .Should().ThrowAsync<OperationCanceledException>();

        var library = new FakeExperienceLibrary { Recalled = [Lesson("from another run", runId: "run-0")] };
        await NewSut(ScriptedValidation(1.0, 1.0), AppendingProposer(), store, mediator: new MetaMemoryMediator("lesson"), library: library)
            .Handle(NewCommand(config), CancellationToken.None);

        library.RecallCalls.Should().BeEmpty("the checkpoint already holds what the run started with");
    }

    [Fact]
    public async Task Handle_ACheckpointWhoseExperienceCarriesAGraderVerdict_IsNotResumed()
    {
        var store = new InMemorySkillTrainingCheckpointStore();
        var config = HardGate(steps: 2, meta: false);
        await NewSut(ScriptedValidation(1.0, 1.0), InterruptedDuringStep(2), store)
            .Invoking(s => s.Handle(NewCommand(config), CancellationToken.None))
            .Should().ThrowAsync<OperationCanceledException>();
        var stored = (await store.GetAsync("run-1", 1, CancellationToken.None))!;
        await store.SaveAsync(stored with { Experience = [Lesson("{\"OverallVerdict\":\"Fail\"}")] }, CancellationToken.None);

        var result = await NewSut(ScriptedValidation(1.0, 1.0), AppendingProposer(), store).Handle(NewCommand(config), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(GraderMarkerScanner.EvidenceLeakCode);
    }

    [Fact]
    public async Task Handle_ACheckpointWhoseEntrySkillTextCarriesAGraderVerdict_IsNotResumed()
    {
        // The rendered lesson omits the skill an entry was drawn against, so the marker is only found by
        // scanning the entry itself.
        var store = new InMemorySkillTrainingCheckpointStore();
        var config = HardGate(steps: 2, meta: false);
        await NewSut(ScriptedValidation(1.0, 1.0), InterruptedDuringStep(2), store)
            .Invoking(s => s.Handle(NewCommand(config), CancellationToken.None))
            .Should().ThrowAsync<OperationCanceledException>();
        var stored = (await store.GetAsync("run-1", 1, CancellationToken.None))!;
        var entry = Lesson("fine") with { SkillContent = "{\"OverallVerdict\":\"Fail\"}" };
        await store.SaveAsync(stored with { Experience = [entry] }, CancellationToken.None);

        var result = await NewSut(ScriptedValidation(1.0, 1.0), AppendingProposer(), store).Handle(NewCommand(config), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_ARunIdReusedUnderAnotherDomain_IsNotResumed()
    {
        var store = new InMemorySkillTrainingCheckpointStore();
        var config = HardGate(steps: 2, meta: false);
        await NewSut(ScriptedValidation(1.0, 1.0), InterruptedDuringStep(2), store)
            .Invoking(s => s.Handle(NewCommand(config), CancellationToken.None))
            .Should().ThrowAsync<OperationCanceledException>();

        var result = await NewSut(ScriptedValidation(1.0, 1.0), AppendingProposer(), store)
            .Handle(NewCommand(config) with { Domain = "finance" }, CancellationToken.None);

        result.IsSuccess.Should().BeFalse("lessons would otherwise be filed under a domain the run did not start in");
    }
}
