using Domain.Common.MetaHarness;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.MetaHarness;

public class TraceScopeTests
{
    [Fact]
    public void ForExecution_CreatesScope_WithNullOptimizationAndCandidateIds()
    {
        var id = Guid.NewGuid();

        var scope = TraceScope.ForExecution(id);

        scope.ExecutionRunId.Should().Be(id);
        scope.OptimizationRunId.Should().BeNull();
        scope.CandidateId.Should().BeNull();
        scope.TaskId.Should().BeNull();
    }

    [Fact]
    public void ResolveDirectory_WithExecutionOnlyScope_ResolvesUnderExecutions()
    {
        var id = Guid.NewGuid();
        var scope = TraceScope.ForExecution(id);

        var dir = scope.ResolveDirectory("/traces");

        dir.Should().Be(Path.Combine("/traces", "executions", id.ToString("D").ToLowerInvariant()));
    }

    [Fact]
    public void ResolveDirectory_WithAllIds_ResolvesToCorrectDirectoryPath()
    {
        var optRunId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();
        var execId = Guid.NewGuid();
        var scope = new TraceScope
        {
            ExecutionRunId = execId,
            OptimizationRunId = optRunId,
            CandidateId = candidateId,
            TaskId = "task-01"
        };

        var dir = scope.ResolveDirectory("/traces");

        var expected = Path.Combine(
            "/traces", "optimizations", optRunId.ToString("D").ToLowerInvariant(),
            "candidates", candidateId.ToString("D").ToLowerInvariant(),
            "eval", "task-01", execId.ToString("D").ToLowerInvariant());
        dir.Should().Be(expected);
    }

    [Fact]
    public void ResolveDirectory_WithOptimizationOnlyScope_ResolvesUnderOptimizations()
    {
        var optRunId = Guid.NewGuid();
        var scope = new TraceScope
        {
            ExecutionRunId = Guid.NewGuid(),
            OptimizationRunId = optRunId
        };

        var dir = scope.ResolveDirectory("/traces");

        dir.Should().Be(Path.Combine("/traces", "optimizations", optRunId.ToString("D").ToLowerInvariant()));
    }

    [Fact]
    public void ResolveDirectory_WithOptimizationAndCandidateButNoTask_ResolvesCorrectly()
    {
        var optRunId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();
        var scope = new TraceScope
        {
            ExecutionRunId = Guid.NewGuid(),
            OptimizationRunId = optRunId,
            CandidateId = candidateId
        };

        var dir = scope.ResolveDirectory("/traces");

        var expected = Path.Combine(
            "/traces", "optimizations", optRunId.ToString("D").ToLowerInvariant(),
            "candidates", candidateId.ToString("D").ToLowerInvariant());
        dir.Should().Be(expected);
    }

    [Fact]
    public void TraceScope_WithExpression_DoesNotMutateOriginal()
    {
        var original = TraceScope.ForExecution(Guid.NewGuid());
        var optRunId = Guid.NewGuid();

        var modified = original with { OptimizationRunId = optRunId };

        original.OptimizationRunId.Should().BeNull();
        modified.OptimizationRunId.Should().Be(optRunId);
    }

    [Fact]
    public void ForExecution_WithEmptyGuid_Throws()
    {
        var act = () => TraceScope.ForExecution(Guid.Empty);
        act.Should().Throw<ArgumentException>().WithParameterName("executionRunId");
    }

    [Fact]
    public void ResolveDirectory_WithCandidateIdButNoOptimizationRunId_Throws()
    {
        var scope = new TraceScope
        {
            ExecutionRunId = Guid.NewGuid(),
            CandidateId = Guid.NewGuid()
        };
        var act = () => scope.ResolveDirectory("/traces");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*CandidateId requires OptimizationRunId*");
    }

    [Fact]
    public void ResolveDirectory_WithTaskIdButNoCandidateId_Throws()
    {
        var scope = new TraceScope
        {
            ExecutionRunId = Guid.NewGuid(),
            OptimizationRunId = Guid.NewGuid(),
            TaskId = "task-01"
        };
        var act = () => scope.ResolveDirectory("/traces");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*TaskId requires CandidateId*");
    }
}
