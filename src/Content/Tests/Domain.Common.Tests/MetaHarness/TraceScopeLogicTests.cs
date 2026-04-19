using Domain.Common.MetaHarness;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.MetaHarness;

/// <summary>
/// Tests for <see cref="TraceScope"/> logic: ForExecution factory, ResolveDirectory
/// across all scope tiers, validation guards, and path traversal defense.
/// </summary>
public class TraceScopeLogicTests
{
    private static readonly string Root = Path.Combine("C:", "traces");
    private static readonly Guid ExecId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OptId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid CandId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    // ── ForExecution ──

    [Fact]
    public void ForExecution_ValidGuid_ReturnsScope()
    {
        var scope = TraceScope.ForExecution(ExecId);

        scope.ExecutionRunId.Should().Be(ExecId);
        scope.OptimizationRunId.Should().BeNull();
        scope.CandidateId.Should().BeNull();
        scope.TaskId.Should().BeNull();
    }

    [Fact]
    public void ForExecution_EmptyGuid_ThrowsArgumentException()
    {
        var act = () => TraceScope.ForExecution(Guid.Empty);

        act.Should().Throw<ArgumentException>()
            .WithParameterName("executionRunId");
    }

    // ── ResolveDirectory — standalone execution ──

    [Fact]
    public void ResolveDirectory_StandaloneExecution_ReturnsExecutionsPath()
    {
        var scope = TraceScope.ForExecution(ExecId);

        var dir = scope.ResolveDirectory(Root);

        dir.Should().Contain("executions");
        dir.Should().Contain(ExecId.ToString("D").ToLowerInvariant());
    }

    // ── ResolveDirectory — optimization without candidate ──

    [Fact]
    public void ResolveDirectory_OptimizationOnly_ReturnsOptimizationsPath()
    {
        var scope = new TraceScope
        {
            ExecutionRunId = ExecId,
            OptimizationRunId = OptId
        };

        var dir = scope.ResolveDirectory(Root);

        dir.Should().Contain("optimizations");
        dir.Should().Contain(OptId.ToString("D").ToLowerInvariant());
        dir.Should().NotContain("candidates");
    }

    // ── ResolveDirectory — with candidate ──

    [Fact]
    public void ResolveDirectory_WithCandidate_ReturnsCandidatePath()
    {
        var scope = new TraceScope
        {
            ExecutionRunId = ExecId,
            OptimizationRunId = OptId,
            CandidateId = CandId
        };

        var dir = scope.ResolveDirectory(Root);

        dir.Should().Contain("candidates");
        dir.Should().Contain(CandId.ToString("D").ToLowerInvariant());
    }

    // ── ResolveDirectory — full hierarchy with TaskId ──

    [Fact]
    public void ResolveDirectory_FullHierarchy_ReturnsEvalPath()
    {
        var scope = new TraceScope
        {
            ExecutionRunId = ExecId,
            OptimizationRunId = OptId,
            CandidateId = CandId,
            TaskId = "task-01"
        };

        var dir = scope.ResolveDirectory(Root);

        dir.Should().Contain("eval");
        dir.Should().Contain("task-01");
        dir.Should().Contain(ExecId.ToString("D").ToLowerInvariant());
    }

    // ── ResolveDirectory — validation ──

    [Fact]
    public void ResolveDirectory_CandidateWithoutOptimization_Throws()
    {
        var scope = new TraceScope
        {
            ExecutionRunId = ExecId,
            CandidateId = CandId
        };

        var act = () => scope.ResolveDirectory(Root);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*CandidateId requires OptimizationRunId*");
    }

    [Fact]
    public void ResolveDirectory_TaskIdWithoutCandidate_Throws()
    {
        var scope = new TraceScope
        {
            ExecutionRunId = ExecId,
            OptimizationRunId = OptId,
            TaskId = "task-01"
        };

        var act = () => scope.ResolveDirectory(Root);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*TaskId requires CandidateId*");
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("task/slash")]
    [InlineData("task\\backslash")]
    public void ResolveDirectory_TaskIdWithPathTraversal_Throws(string taskId)
    {
        var scope = new TraceScope
        {
            ExecutionRunId = ExecId,
            OptimizationRunId = OptId,
            CandidateId = CandId,
            TaskId = taskId
        };

        var act = () => scope.ResolveDirectory(Root);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*invalid path characters*");
    }
}
