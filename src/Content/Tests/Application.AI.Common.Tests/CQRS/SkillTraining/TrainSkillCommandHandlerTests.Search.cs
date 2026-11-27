using Application.AI.Common.Services.SkillTraining;
using Application.AI.Common.Services.SkillTraining.Schedulers;
using Domain.AI.SkillTraining;
using FluentAssertions;
using StubProposer = Tests.Common.SkillTraining.ScriptedPatchProposer;
using StubRolloutRunner = Tests.Common.SkillTraining.ScriptedRolloutRunner;

namespace Application.AI.Common.Tests.CQRS.SkillTraining;

/// <summary>
/// How the loop searches: which items each step is scored on, what a candidate has to beat, and what
/// the proposer is told about the size of change a step can keep.
/// </summary>
public partial class TrainSkillCommandHandlerTests
{
    private const string NewRule = "- new rule";

    private static StubProposer NewRuleProposer() =>
        new(_ => new Patch { Edits = [new Edit { Op = EditOp.Append, Content = NewRule }] });

    private static TrainSkillConfig SearchConfig(int steps = 1, int seed = 0) => new()
    {
        Epochs = 1, StepsPerEpoch = steps, LrStart = 4, LrMin = 1,
        LrScheduler = "constant", Patience = 10, Seed = seed,
        UseSlowUpdate = false, UseMetaSkill = false
    };

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public async Task Handle_EachStep_ReflectsOnFreshTrainingCasesButScoresTheSameValidationCases(int seed)
    {
        // A seeded sampler handed the same seed returns the same items, so a seed that never changes
        // shows the proposer the same few training cases on every step of the run. Validation is the
        // opposite: the gate compares each candidate with a score taken on validation, so those items
        // must stay put or the comparison is between different exams.
        var batches = new List<RolloutBatch>();
        var runner = new StubRolloutRunner((_, batch) =>
        {
            batches.Add(batch);
            return [new RolloutResult { ItemId = "x", Hard = 0.0, Soft = 0.0 }];
        });
        var sut = NewSut(runner, NewRuleProposer(), new InMemorySkillTrainingCheckpointStore());

        await sut.Handle(NewCommand(SearchConfig(steps: 3, seed: seed)), CancellationToken.None);

        // The candidate's held-in rollout pins item ids and samples nothing, so only free train draws count.
        var trainSeeds = batches.Where(b => b.Split == "train" && b.ItemIds.Count == 0).Select(b => b.Seed).ToList();
        trainSeeds.Should().HaveCount(3).And.OnlyHaveUniqueItems(
            "each step's proposer should see a fresh draw of training cases");
        batches.Where(b => b.Split == "val").Select(b => b.Seed).Should().AllBeEquivalentTo(seed,
            "every validation score in a run must come from the same cases");
    }

    [Fact]
    public async Task Handle_CandidateScoresBelowTheStartingSkill_IsRejected()
    {
        // The starting skill passes all of validation; the candidate passes half. Both do the same on the
        // training cases. Without a score for the starting skill the first candidate is compared with 0
        // and kept, though it is worse than what the run began with.
        var runner = new StubRolloutRunner((skill, batch) => batch.Split switch
        {
            "val" => [new RolloutResult { ItemId = "v1", Hard = 1.0, Soft = 1.0 },
                      new RolloutResult { ItemId = "v2", Hard = skill.Contains(NewRule) ? 0.0 : 1.0, Soft = 0.0 }],
            _ => [new RolloutResult { ItemId = "t1", Hard = 0.5, Soft = 0.5 }]
        });
        var sut = NewSut(runner, NewRuleProposer(), new InMemorySkillTrainingCheckpointStore());

        var result = await sut.Handle(NewCommand(SearchConfig()), CancellationToken.None);

        result.Value!.Steps.Single().Action.Should().Be(GateAction.Reject);
        result.Value.BestSkill.Should().NotContain(NewRule);
    }

    [Fact]
    public async Task Handle_NothingAccepted_ReportsTheStartingSkillsOwnScore()
    {
        var runner = new StubRolloutRunner((_, batch) => batch.Split == "val"
            ? [new RolloutResult { ItemId = "v1", Hard = 1.0, Soft = 1.0 },
               new RolloutResult { ItemId = "v2", Hard = 0.0, Soft = 0.0 }]
            : [new RolloutResult { ItemId = "t1", Hard = 0.0, Soft = 0.0 }]);
        var sut = NewSut(runner, NewRuleProposer(), new InMemorySkillTrainingCheckpointStore());

        var result = await sut.Handle(NewCommand(SearchConfig(steps: 2)), CancellationToken.None);

        result.Value!.HasAcceptedAny.Should().BeFalse();
        result.Value.BestScore.Should().Be(0.5, "the best skill is the starting one, and that is its validation score");
    }

    [Fact]
    public async Task Handle_Resuming_DoesNotScoreTheStartingSkillAgain()
    {
        var store = new InMemorySkillTrainingCheckpointStore();
        var command = NewCommand(SearchConfig(steps: 3));
        var interrupted = NewSut(new StubRolloutRunner((s, b) => AcceptingRollouts(s, b.Split)), InterruptedDuringStepTwo(), store);
        await FluentActions.Awaiting(() => interrupted.Handle(command, CancellationToken.None))
            .Should().ThrowAsync<OperationCanceledException>();

        var startingSkillScored = 0;
        var runner = new StubRolloutRunner((skill, batch) =>
        {
            if (batch.Split == "val" && skill == command.InitialSkill) startingSkillScored++;
            return AcceptingRollouts(skill, batch.Split);
        });
        await NewSut(runner, AppendingProposer(), store).Handle(command, CancellationToken.None);

        startingSkillScored.Should().Be(0, "the checkpoint already holds the score the run carries forward");
    }

    [Fact]
    public async Task Handle_ResumingARunThatAcceptedNothing_ReportsTheStartingSkillsScore()
    {
        // Every candidate scores below the starting skill, so nothing is accepted. The resumed run has no
        // promoted checkpoint to take its best score from, and must not fall back to 0.
        var store = new InMemorySkillTrainingCheckpointStore();
        var command = NewCommand(SearchConfig(steps: 3));
        StubRolloutRunner WorseThanStart() => new((skill, batch) => batch.Split == "val"
            ? [new RolloutResult { ItemId = "v1", Hard = skill == command.InitialSkill ? 1.0 : 0.0, Soft = 0.0 }]
            : [new RolloutResult { ItemId = "t1", Hard = 0.0, Soft = 0.0 }]);
        await FluentActions.Awaiting(() => NewSut(WorseThanStart(), InterruptedDuringStepTwo(), store).Handle(command, CancellationToken.None))
            .Should().ThrowAsync<OperationCanceledException>();

        var resumed = await NewSut(WorseThanStart(), AppendingProposer(), store).Handle(command, CancellationToken.None);

        resumed.Value!.HasAcceptedAny.Should().BeFalse();
        resumed.Value.BestScore.Should().Be(1.0, "an uninterrupted run reports the starting skill's score, and so must a resumed one");
    }

    [Fact]
    public async Task Handle_ResumingAfterACandidateThatTiedTheStart_ReportsTheSameBestAsAnUninterruptedRun()
    {
        // The candidate ties the starting skill on validation and does better on training, so the gate
        // keeps it as current without promoting it. The best skill is still the starting one, at step 0,
        // and a resume must not promote the tied candidate in its place.
        StubRolloutRunner TieOnValidation() => new((skill, batch) => batch.Split == "val"
            ? [new RolloutResult { ItemId = "v1", Hard = 0.5, Soft = 0.5 }]
            : [new RolloutResult { ItemId = "t1", Hard = skill.Contains("- a rule") ? 1.0 : 0.0, Soft = 0.0 }]);
        var command = NewCommand(SearchConfig(steps: 3));
        var uninterrupted = await NewSut(TieOnValidation(), AppendingProposer(), new InMemorySkillTrainingCheckpointStore())
            .Handle(command, CancellationToken.None);
        var store = new InMemorySkillTrainingCheckpointStore();
        await FluentActions.Awaiting(() => NewSut(TieOnValidation(), InterruptedDuringStepTwo(), store).Handle(command, CancellationToken.None))
            .Should().ThrowAsync<OperationCanceledException>();

        var resumed = await NewSut(TieOnValidation(), AppendingProposer(), store).Handle(command, CancellationToken.None);

        uninterrupted.Value!.Steps[0].Action.Should().Be(GateAction.Accept, "the scenario needs a kept-but-not-promoted candidate");
        (resumed.Value!.BestSkill, resumed.Value.BestStep).Should().Be((uninterrupted.Value.BestSkill, uninterrupted.Value.BestStep));
    }

    [Fact]
    public async Task Handle_ResumingALongRunWhosePromotionWasEvicted_ReportsThePromotedSkillAndScore()
    {
        // Step 1 is promoted and every later candidate is worse, so each later checkpoint repeats the
        // promoted score. The shipped store keeps 64 checkpoints and, among equal scores, the latest, so by
        // step 70 the promoted checkpoint has been evicted and only rejects remain. A resumed run must
        // still report the promoted skill with its score, not the starting skill with the promoted score.
        StubRolloutRunner OneRuleIsBest(string initial) => new((skill, batch) => batch.Split == "val"
            ? [new RolloutResult { ItemId = "v1", Hard = skill == initial ? 0.5 : skill.Split(NewRule.TrimStart('-', ' ')).Length == 2 ? 1.0 : 0.0, Soft = 0.0 }]
            : [new RolloutResult { ItemId = "t1", Hard = 0.0, Soft = 0.0 }]);
        var command = NewCommand(SearchConfig(steps: 80) with { Patience = 100 });
        var uninterrupted = await NewSut(OneRuleIsBest(command.InitialSkill), NewRuleProposer(), new InMemorySkillTrainingCheckpointStore())
            .Handle(command, CancellationToken.None);
        var store = new InMemorySkillTrainingCheckpointStore();
        var calls = 0;
        var interruptAtSeventy = new StubProposer(_ => ++calls == 70
            ? throw new OperationCanceledException()
            : new Patch { Edits = [new Edit { Op = EditOp.Append, Content = NewRule }] });
        await FluentActions.Awaiting(() => NewSut(OneRuleIsBest(command.InitialSkill), interruptAtSeventy, store).Handle(command, CancellationToken.None))
            .Should().ThrowAsync<OperationCanceledException>();

        var resumed = await NewSut(OneRuleIsBest(command.InitialSkill), NewRuleProposer(), store).Handle(command, CancellationToken.None);

        uninterrupted.Value!.BestStep.Should().Be(1, "the scenario needs one early promotion and nothing after it");
        (await store.GetAsync(command.RunId, 1, CancellationToken.None)).Should().BeNull("the scenario needs the promoted checkpoint evicted");
        (resumed.Value!.BestSkill, resumed.Value.BestScore).Should().Be((uninterrupted.Value.BestSkill, uninterrupted.Value.BestScore));
    }

    [Fact]
    public async Task Handle_ResumingALongRunThatNeverMovedOffTheStart_ReportsNothingAccepted()
    {
        // Every candidate is worse, so every checkpoint holds the starting skill and its score. By step 70
        // the store has dropped the oldest ones, which leaves a gap; the highest-scoring checkpoint left is
        // a reject of the starting skill, and must not be taken for a promotion.
        var command = NewCommand(SearchConfig(steps: 80) with { Patience = 100 });
        StubRolloutRunner WorseThanStart() => new((skill, batch) => batch.Split == "val"
            ? [new RolloutResult { ItemId = "v1", Hard = skill == command.InitialSkill ? 0.5 : 0.0, Soft = 0.0 }]
            : [new RolloutResult { ItemId = "t1", Hard = 0.0, Soft = 0.0 }]);
        var store = new InMemorySkillTrainingCheckpointStore();
        await FluentActions.Awaiting(() => NewSut(WorseThanStart(), InterruptedDuringStep(70), store).Handle(command, CancellationToken.None))
            .Should().ThrowAsync<OperationCanceledException>();

        var resumed = await NewSut(WorseThanStart(), AppendingProposer(), store).Handle(command, CancellationToken.None);

        (await store.GetAsync(command.RunId, 1, CancellationToken.None)).Should().BeNull("the scenario needs a gap in the history");
        (resumed.Value!.HasAcceptedAny, resumed.Value.BestStep, resumed.Value.BestSkill, resumed.Value.BestScore)
            .Should().Be((false, 0, command.InitialSkill, 0.5));
    }

    [Fact]
    public async Task Handle_CheckpointsFromBeforeTheStartingScoreExisted_RefusesRatherThanResuming()
    {
        // A run saved by the earlier loop compared candidates with 0 and drew the same training cases each
        // step. Resuming it here would splice two sets of rules into one run, so it is refused like any
        // other mismatched run.
        var command = NewCommand(SearchConfig(steps: 3));
        var earlierFingerprint = SkillContentHash.Of(string.Join('\n',
            command.SkillId, command.Domain, SkillContentHash.Of(command.InitialSkill),
            System.Text.Json.JsonSerializer.Serialize(command.Config with { Epochs = 0, Patience = 0 })));
        var store = new InMemorySkillTrainingCheckpointStore();
        await store.SaveAsync(new SkillTrainingCheckpoint
        {
            RunId = command.RunId, SkillId = command.SkillId, Step = 1, Epoch = 1,
            SkillContent = command.InitialSkill, SkillHash = SkillContentHash.Of(command.InitialSkill),
            Score = 0.0, Action = GateAction.Reject, RunFingerprint = earlierFingerprint, CreatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);

        var result = await NewSut(new StubRolloutRunner((s, b) => AcceptingRollouts(s, b.Split)), AppendingProposer(), store)
            .Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Contains("earlier version of the training loop"));
    }

    [Fact]
    public async Task Handle_CosineSchedule_TellsTheProposerEachStepsEditBudget()
    {
        // The selector keeps this many of a proposal's edits. A proposer that is not told the number
        // writes a whole change and sees only its first few places applied.
        var budgets = new List<int?>();
        var proposer = new StubProposer(input =>
        {
            budgets.Add(input.EditBudget);
            return new Patch { Edits = [new Edit { Op = EditOp.Append, Content = NewRule }] };
        });
        var runner = new StubRolloutRunner((_, _) => [new RolloutResult { ItemId = "x", Hard = 0.0, Soft = 0.0 }]);
        var sut = NewSut(runner, proposer, new InMemorySkillTrainingCheckpointStore());

        await sut.Handle(
            NewCommand(SearchConfig(steps: 5) with { LrScheduler = "cosine", LrStart = 8, LrMin = 1 }),
            CancellationToken.None);

        var cosine = new CosineScheduler();
        budgets.Should().Equal(Enumerable.Range(0, 5).Select(step => (int?)cosine.GetLearningRate(step, 5, 8, 1)));
    }
}
