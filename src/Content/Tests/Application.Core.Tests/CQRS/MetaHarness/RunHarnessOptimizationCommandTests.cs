using Application.Core.CQRS.MetaHarness;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.CQRS.MetaHarness;

/// <summary>
/// Unit tests for <see cref="RunHarnessOptimizationCommand"/> and
/// <see cref="OptimizationResult"/> record types.
/// Verifies default values, optional properties, and record behavior.
/// </summary>
public class RunHarnessOptimizationCommandTests
{
    [Fact]
    public void SeedCandidateId_DefaultValue_IsNull()
    {
        var command = new RunHarnessOptimizationCommand
        {
            OptimizationRunId = Guid.NewGuid()
        };

        command.SeedCandidateId.Should().BeNull();
    }

    [Fact]
    public void MaxIterations_DefaultValue_IsNull()
    {
        var command = new RunHarnessOptimizationCommand
        {
            OptimizationRunId = Guid.NewGuid()
        };

        command.MaxIterations.Should().BeNull();
    }

    [Fact]
    public void WithExpression_SetsSeedCandidateId()
    {
        var seedId = Guid.NewGuid();
        var command = new RunHarnessOptimizationCommand
        {
            OptimizationRunId = Guid.NewGuid()
        } with { SeedCandidateId = seedId };

        command.SeedCandidateId.Should().Be(seedId);
    }

    [Fact]
    public void WithExpression_SetsMaxIterations()
    {
        var command = new RunHarnessOptimizationCommand
        {
            OptimizationRunId = Guid.NewGuid()
        } with { MaxIterations = 5 };

        command.MaxIterations.Should().Be(5);
    }

    // --- OptimizationResult ---

    [Fact]
    public void OptimizationResult_BestCandidateId_DefaultIsNull()
    {
        var result = new OptimizationResult
        {
            OptimizationRunId = Guid.NewGuid(),
            ProposedChangesPath = string.Empty
        };

        result.BestCandidateId.Should().BeNull();
    }

    [Fact]
    public void OptimizationResult_BestScore_DefaultIsZero()
    {
        var result = new OptimizationResult
        {
            OptimizationRunId = Guid.NewGuid(),
            ProposedChangesPath = string.Empty
        };

        result.BestScore.Should().Be(0.0);
    }

    [Fact]
    public void OptimizationResult_IterationCount_DefaultIsZero()
    {
        var result = new OptimizationResult
        {
            OptimizationRunId = Guid.NewGuid(),
            ProposedChangesPath = string.Empty
        };

        result.IterationCount.Should().Be(0);
    }

    [Fact]
    public void OptimizationResult_EarlyStopReason_DefaultIsNull()
    {
        var result = new OptimizationResult
        {
            OptimizationRunId = Guid.NewGuid(),
            ProposedChangesPath = string.Empty
        };

        result.EarlyStopReason.Should().BeNull();
    }

    [Fact]
    public void OptimizationResult_FullyPopulated_HasExpectedValues()
    {
        var runId = Guid.NewGuid();
        var bestId = Guid.NewGuid();

        var result = new OptimizationResult
        {
            OptimizationRunId = runId,
            BestCandidateId = bestId,
            BestScore = 0.95,
            IterationCount = 7,
            ProposedChangesPath = "/tmp/optimizations/proposed",
            EarlyStopReason = "no_improvement"
        };

        result.OptimizationRunId.Should().Be(runId);
        result.BestCandidateId.Should().Be(bestId);
        result.BestScore.Should().Be(0.95);
        result.IterationCount.Should().Be(7);
        result.ProposedChangesPath.Should().Be("/tmp/optimizations/proposed");
        result.EarlyStopReason.Should().Be("no_improvement");
    }
}
