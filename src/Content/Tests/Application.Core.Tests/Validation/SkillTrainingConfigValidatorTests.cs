using Application.Core.Validation;
using Domain.Common.Config.AI.SkillTraining;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.Validation;

/// <summary>
/// Pins <see cref="SkillTrainingConfigValidator"/> (#843): the work description describes the work and not
/// its measurement, because naming the measurement invites the proposer to write for it.
/// </summary>
public sealed class SkillTrainingConfigValidatorTests
{
    private readonly SkillTrainingConfigValidator _sut = new();

    [Fact]
    public void Defaults_AreValid()
    {
        _sut.Validate(new SkillTrainingConfig()).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("Grade each submission against the rubric.")]
    [InlineData("Maximise the score on the benchmark.")]
    [InlineData("The grader checks totals.")]
    [InlineData("Improve our SCORES")]
    [InlineData("grading is strict")]
    public void WorkDescription_NamingTheMeasurement_IsRejected(string text)
    {
        var result = _sut.Validate(new SkillTrainingConfig { WorkDescription = text });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == nameof(SkillTrainingConfig.WorkDescription));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Reconcile vendor invoices against purchase orders and flag mismatches.")]
    [InlineData("Risk evaluation for supplier onboarding; the scoreboard is someone else's concern.")]
    public void WorkDescription_DescribingTheWork_IsAccepted(string text)
    {
        _sut.Validate(new SkillTrainingConfig { WorkDescription = text }).IsValid.Should().BeTrue();
    }

    [Fact]
    public void WorkDescription_OverTheLimit_IsRejected()
    {
        var text = new string('a', SkillTrainingConfigValidator.MaxWorkDescriptionLength + 1);

        _sut.Validate(new SkillTrainingConfig { WorkDescription = text }).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(SkillTrainingConfigValidator.MaxProposerTurnsCeiling + 1)]
    public void MaxProposerTurns_OutsideTheRange_IsRejected(int turns)
    {
        _sut.Validate(new SkillTrainingConfig { MaxProposerTurns = turns }).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Skill Optimizer")]
    [InlineData("../escape")]
    [InlineData("-leading-dash")]
    public void ProposerAgentName_ThatIsNotASimpleName_IsRejected(string name)
    {
        _sut.Validate(new SkillTrainingConfig { ProposerAgentName = name }).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Worker Agent")]
    [InlineData("../escape")]
    public void RolloutAgentName_WhenADatasetIsConfigured_MustBeASimpleName(string name)
    {
        var config = new SkillTrainingConfig { DatasetPath = "cases.yaml", RolloutAgentName = name };

        _sut.Validate(config).Errors.Should().ContainSingle(e => e.PropertyName == nameof(SkillTrainingConfig.RolloutAgentName));
    }

    [Fact]
    public void RolloutAgentName_WithNoDataset_NeedsNothing()
    {
        _sut.Validate(new SkillTrainingConfig { DatasetPath = "", RolloutAgentName = "" }).IsValid.Should().BeTrue();
    }

    [Fact]
    public void RolloutAgentName_WithADataset_IsAccepted()
    {
        _sut.Validate(new SkillTrainingConfig { DatasetPath = "cases.yaml", RolloutAgentName = "worker" }).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(SkillTrainingConfigValidator.MaxRolloutConcurrencyCeiling + 1)]
    public void MaxRolloutConcurrency_OutsideTheRange_IsRejected(int concurrency)
    {
        _sut.Validate(new SkillTrainingConfig { MaxRolloutConcurrency = concurrency }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void EvidenceRoot_Blank_IsRejected()
    {
        _sut.Validate(new SkillTrainingConfig { EvidenceRoot = "  " }).IsValid.Should().BeFalse();
    }
}
