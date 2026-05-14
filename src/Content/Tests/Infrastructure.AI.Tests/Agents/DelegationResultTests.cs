using Domain.AI.Governance;
using Domain.AI.Orchestration;
using FluentAssertions;
using Xunit;

namespace Infrastructure.AI.Tests.Agents;

public sealed class DelegationResultTests
{
    [Fact]
    public void Success_CreatesResultWithIsSuccessTrue_AndPopulatedFields()
    {
        // Arrange & Act
        var result = DelegationResult.Success("output text", tokensUsed: 150, durationMs: 2500);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Output.Should().Be("output text");
        result.TokensUsed.Should().Be(150);
        result.DurationMs.Should().Be(2500);
        result.FailureReason.Should().BeNull();
        result.AutonomyExceeded.Should().BeNull();
    }

    [Fact]
    public void Fail_CreatesResultWithIsSuccessFalse_AndPopulatedFailureReason()
    {
        // Arrange & Act
        var result = DelegationResult.Fail("agent timed out");

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.FailureReason.Should().Be("agent timed out");
        result.Output.Should().BeNull();
    }

    [Fact]
    public void FailAutonomyExceeded_CreatesResultWithPopulatedAutonomyExceeded()
    {
        // Arrange
        var exceeded = new AutonomyExceededResult
        {
            AttemptedAction = "bash",
            CurrentLevel = AutonomyLevel.Restricted,
            RequiredLevel = AutonomyLevel.Autonomous,
            Reason = "tier violation"
        };

        // Act
        var result = DelegationResult.FailAutonomyExceeded(exceeded);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.AutonomyExceeded.Should().NotBeNull();
        result.AutonomyExceeded!.AttemptedAction.Should().Be("bash");
        result.AutonomyExceeded.CurrentLevel.Should().Be(AutonomyLevel.Restricted);
        result.AutonomyExceeded.RequiredLevel.Should().Be(AutonomyLevel.Autonomous);
        result.FailureReason.Should().Contain("tier violation");
    }
}
