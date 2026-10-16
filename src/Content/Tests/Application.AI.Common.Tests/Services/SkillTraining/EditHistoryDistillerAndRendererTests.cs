using Application.AI.Common.Services.SkillTraining;
using Domain.AI.SkillTraining;
using FluentAssertions;

namespace Application.AI.Common.Tests.Services.SkillTraining;

/// <summary>
/// Pins <see cref="EditHistoryDistiller"/>, <see cref="ExperienceRenderer"/> and the outcome types (#845):
/// the lesson is exactly what the gate's verdicts support, and the proposer reads it without a score.
/// </summary>
public sealed class EditHistoryDistillerAndRendererTests
{
    private static DistillationInput Input(IReadOnlyList<EditTrial>? trials = null, string guidance = "") => new()
    {
        RunId = "run-A",
        SkillId = "skill-X",
        Domain = "finance",
        Epoch = 2,
        CurrentSkill = "# skill\n- rule",
        Trials = trials ?? [],
        PairedGuidance = guidance
    };

    private static ExperienceEntry Written(DistillationOutcome outcome) =>
        outcome.Should().BeOfType<EntryWritten>().Subject.Entry;

    [Fact]
    public async Task Distill_NothingTriedAndNothingCompared_IsNoEntryWithAReason()
    {
        var outcome = await new EditHistoryDistiller().DistillAsync(Input(), CancellationToken.None);

        outcome.Should().BeOfType<NoEntry>().Which.Reason.Should().Contain("nothing to learn");
    }

    [Fact]
    public async Task Distill_PutsEachVerdictInPlainWordsAndKeepsTheTrials()
    {
        EditTrial[] trials =
        [
            new(1, "Append \"- keep\"", EditVerdict.Helped),
            new(2, "Delete \"- old\"", EditVerdict.Hurt),
            new(3, "Append \"- noop\"", EditVerdict.NoObservedEffect),
            new(4, "Append \"- a\"", EditVerdict.Confounded),
            new(4, "Append \"- b\"", EditVerdict.Confounded)
        ];

        var entry = Written(await new EditHistoryDistiller().DistillAsync(Input(trials), CancellationToken.None));

        entry.Lesson.Should().Contain("Kept: Append \"- keep\"")
            .And.Contain("Made it worse, do not repeat: Delete \"- old\"")
            .And.Contain("Changed nothing, not worth repeating: Append \"- noop\"")
            .And.Contain("Tried together with other edits, unclear which mattered: Append \"- a\"");
        entry.EditVerdicts.Should().Equal(trials);
        entry.Evidence.Should().Be("Epoch 2: 5 edit(s) were tried: 1 kept, 1 made it worse, 1 changed nothing, 2 unclear.");
        entry.Confidence.Should().BeApproximately(3.0 / 5.0, 1e-9, "confounded edits cannot be attributed");
    }

    [Fact]
    public async Task Distill_StampsTheEntryWithItsPositionAndTheSkillAsItStands()
    {
        var entry = Written(await new EditHistoryDistiller().DistillAsync(
            Input([new EditTrial(1, "x", EditVerdict.Helped)]), CancellationToken.None));

        (entry.RunId, entry.SkillId, entry.Domain, entry.Epoch).Should().Be(("run-A", "skill-X", "finance", 2));
        entry.SkillContent.Should().Be("# skill\n- rule");
        entry.SkillHash.Should().Be(SkillContentHash.Of("# skill\n- rule"));
        entry.Cue.Should().Contain("skill-X").And.Contain("finance");
    }

    [Fact]
    public async Task Distill_OnlyAPairedComparison_BecomesTheLessonAtLowConfidence()
    {
        var entry = Written(await new EditHistoryDistiller().DistillAsync(
            Input(guidance: "Items 3 and 5 regressed on the re-run."), CancellationToken.None));

        entry.Lesson.Should().Be("Items 3 and 5 regressed on the re-run.");
        entry.Confidence.Should().Be(0.5);
        entry.EditVerdicts.Should().BeEmpty();
    }

    [Fact]
    public async Task Distill_TrialsAndAComparison_KeepsBoth()
    {
        var entry = Written(await new EditHistoryDistiller().DistillAsync(
            Input([new EditTrial(1, "x", EditVerdict.Helped)], guidance: "Item 3 regressed."), CancellationToken.None));

        entry.Evidence.Should().EndWith("Paired re-run: Item 3 regressed.");
    }

    [Fact]
    public async Task Distill_ManyTrials_ListsTheFirstAndCountsTheRest()
    {
        var trials = Enumerable.Range(1, EditHistoryDistiller.MaxListedTrials + 3)
            .Select(i => new EditTrial(i, $"edit {i}", EditVerdict.Helped)).ToList();

        var entry = Written(await new EditHistoryDistiller().DistillAsync(Input(trials), CancellationToken.None));

        entry.Lesson.Should().Contain("edit 20").And.NotContain("edit 21").And.EndWith("- (and 3 more)");
        entry.EditVerdicts.Should().HaveCount(23, "the record keeps every trial even when the text lists fewer");
    }

    [Fact]
    public void Outcome_FactoriesRefuseEmptyInput()
    {
        var noReason = () => DistillationOutcome.None(" ");
        var noEntry = () => DistillationOutcome.Written(null!);

        noReason.Should().Throw<ArgumentException>();
        noEntry.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Render_NoEntries_IsEmpty()
    {
        ExperienceRenderer.Render([]).Should().BeEmpty();
    }

    [Fact]
    public async Task Render_ShowsTheCueTheLessonTheBasisAndTheConfidence_ButNotTheSkillOrAScore()
    {
        var entry = Written(await new EditHistoryDistiller().DistillAsync(
            Input([new EditTrial(1, "Append \"- keep\"", EditVerdict.Helped)]), CancellationToken.None));

        var text = ExperienceRenderer.Render([entry]);

        text.Should().Contain("finance, epoch 2").And.Contain("When: ").And.Contain("Kept: Append \"- keep\"")
            .And.Contain("Basis: Epoch 2:").And.Contain("Confidence: 1");
        text.Should().NotContain("# skill").And.NotContain(entry.SkillHash);
        text.ToLowerInvariant().Should().NotContain("score");
    }

    [Fact]
    public void Render_OnlyTheMostRecentEntriesAreShown()
    {
        var entries = Enumerable.Range(1, ExperienceRenderer.MaxEntries + 2).Select(i => new ExperienceEntry
        {
            Cue = "c", Lesson = $"lesson {i}", Confidence = 1, SkillHash = "h", SkillContent = "s",
            Domain = "d", SkillId = "s", RunId = "r", Epoch = i
        }).ToList();

        var text = ExperienceRenderer.Render(entries);

        text.Should().NotContain("lesson 1\n").And.NotContain("lesson 2\n").And.Contain("lesson 3").And.Contain($"lesson {ExperienceRenderer.MaxEntries + 2}");
    }

    [Fact]
    public void SkillContentHash_IsSha256LowerHexOfTheUtf8Text()
    {
        SkillContentHash.Of("").Should().Be("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
        SkillContentHash.Of("abc").Should().Be("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }
}
