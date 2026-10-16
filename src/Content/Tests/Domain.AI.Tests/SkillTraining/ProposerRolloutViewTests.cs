using Domain.AI.SkillTraining;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.SkillTraining;

/// <summary>
/// Pins the point of <see cref="ProposerRolloutView"/> (#841): the proposer sees whether a rollout
/// passed and what happened, and never a number the grader produced.
/// </summary>
public sealed class ProposerRolloutViewTests
{
    [Fact]
    public void Type_HasNoNumericMember_SoNoScoreCanReachTheProposer()
    {
        var numeric = new[] { typeof(double), typeof(float), typeof(decimal), typeof(int), typeof(long) };

        var offending = typeof(ProposerRolloutView).GetProperties()
            .Where(p => numeric.Contains(p.PropertyType))
            .Select(p => p.Name);

        offending.Should().BeEmpty("a score on this type would put the grader's verdict in the proposer's context");
    }

    [Theory]
    [InlineData(1.0, 0.0, true)]
    [InlineData(0.0, 0.9, false)]
    [InlineData(0.5, 0.5, false)]
    public void From_PassedIsTheBinaryOutcomeNotThePartialCredit(double hard, double soft, bool expectedPassed)
    {
        var view = ProposerRolloutView.From(
            new RolloutResult { ItemId = "i", Hard = hard, Soft = soft, Trajectory = "t", TaskType = "k" });

        view.Should().Be(new ProposerRolloutView("i", expectedPassed, "t", "k"));
    }
}
