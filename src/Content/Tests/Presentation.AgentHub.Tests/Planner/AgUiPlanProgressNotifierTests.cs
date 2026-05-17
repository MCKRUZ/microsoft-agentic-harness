using Domain.AI.Planner;
using Domain.AI.Sandbox;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Presentation.AgentHub.AgUi;
using Presentation.AgentHub.Planner;
using Xunit;

namespace Presentation.AgentHub.Tests.Planner;

/// <summary>
/// Tests for <see cref="AgUiPlanProgressNotifier"/> -- verifies correct domain-to-AG-UI
/// event translation and fire-and-forget writer behavior.
/// </summary>
public sealed class AgUiPlanProgressNotifierTests
{
    private readonly Mock<IAgUiEventWriterAccessor> _accessorMock = new();
    private readonly Mock<IAgUiEventWriter> _writerMock = new();
    private readonly AgUiPlanProgressNotifier _sut;

    public AgUiPlanProgressNotifierTests()
    {
        _accessorMock.Setup(a => a.Writer).Returns(_writerMock.Object);
        _writerMock
            .Setup(w => w.WriteAsync(It.IsAny<AgUiEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _sut = new AgUiPlanProgressNotifier(
            _accessorMock.Object,
            NullLogger<AgUiPlanProgressNotifier>.Instance);
    }

    [Fact]
    public async Task PlanStarted_EmitsPlanStartedEvent()
    {
        var planId = PlanId.New();
        var graph = CreateMinimalGraph(planId, 3);

        await _sut.NotifyPlanStartedAsync(planId, "Test Plan", graph, CancellationToken.None);

        _writerMock.Verify(
            w => w.WriteAsync(It.Is<PlanStartedEvent>(e =>
                e.PlanId == planId.Value.ToString() &&
                e.PlanName == "Test Plan" &&
                e.TotalSteps == 3),
            CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task StepStarted_EmitsStepStartedEvent()
    {
        var planId = PlanId.New();
        var stepId = PlanStepId.New();

        await _sut.NotifyStepStartedAsync(planId, stepId, "Analyze Code", StepType.LlmCall, CancellationToken.None);

        _writerMock.Verify(
            w => w.WriteAsync(It.Is<PlanStepStartedEvent>(e =>
                e.PlanId == planId.Value.ToString() &&
                e.StepId == stepId.Value.ToString() &&
                e.StepName == "Analyze Code" &&
                e.StepType == "LlmCall"),
            CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task StepCompleted_EmitsStepCompletedEvent()
    {
        var planId = PlanId.New();
        var stepId = PlanStepId.New();
        var duration = TimeSpan.FromSeconds(5.5);

        await _sut.NotifyStepCompletedAsync(planId, stepId, StepExecutionStatus.Completed, duration, "Result output", CancellationToken.None);

        _writerMock.Verify(
            w => w.WriteAsync(It.Is<PlanStepCompletedEvent>(e =>
                e.PlanId == planId.Value.ToString() &&
                e.StepId == stepId.Value.ToString() &&
                e.Status == "Completed" &&
                e.DurationMs == 5500 &&
                e.OutputSummary == "Result output"),
            CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task StepCompleted_LongOutput_TruncatesTo500Chars()
    {
        var planId = PlanId.New();
        var stepId = PlanStepId.New();
        var longOutput = new string('x', 1000);

        await _sut.NotifyStepCompletedAsync(planId, stepId, StepExecutionStatus.Completed, TimeSpan.Zero, longOutput, CancellationToken.None);

        _writerMock.Verify(
            w => w.WriteAsync(It.Is<PlanStepCompletedEvent>(e =>
                e.OutputSummary!.Length == 500),
            CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task StateUpdate_EmitsStateDeltaEvent()
    {
        var planId = PlanId.New();
        var stepId = PlanStepId.New();

        await _sut.NotifyStateUpdateAsync(planId, stepId, StepExecutionStatus.Pending, StepExecutionStatus.Running, CancellationToken.None);

        _writerMock.Verify(
            w => w.WriteAsync(It.Is<PlanStateUpdateEvent>(e =>
                e.PlanId == planId.Value.ToString() &&
                e.Patch.Count == 1 &&
                e.Patch[0].Op == "replace" &&
                e.Patch[0].Path == $"/steps/{stepId.Value}/status"),
            CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task SandboxStatus_EmitsSandboxStatusEvent()
    {
        var planId = PlanId.New();
        var stepId = PlanStepId.New();
        var usage = new ResourceUsage
        {
            MemoryBytes = 1024 * 1024,
            CpuTimeSeconds = 2.5,
            WallClockDuration = TimeSpan.FromSeconds(3),
        };

        await _sut.NotifySandboxStatusAsync(planId, stepId, "file_system", SandboxIsolationLevel.Process, usage, "abc123", CancellationToken.None);

        _writerMock.Verify(
            w => w.WriteAsync(It.Is<SandboxStatusEvent>(e =>
                e.PlanId == planId.Value.ToString() &&
                e.StepId == stepId.Value.ToString() &&
                e.ToolName == "file_system" &&
                e.IsolationLevel == "Process" &&
                e.MemoryUsedBytes == 1024 * 1024 &&
                e.CpuTimeMs == 2500 &&
                e.AttestationHash == "abc123"),
            CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task SandboxStatus_NullAttestationHash_PassesNull()
    {
        var planId = PlanId.New();
        var stepId = PlanStepId.New();
        var usage = new ResourceUsage
        {
            MemoryBytes = 512,
            CpuTimeSeconds = 0.1,
            WallClockDuration = TimeSpan.FromMilliseconds(200),
        };

        await _sut.NotifySandboxStatusAsync(planId, stepId, "calculator", SandboxIsolationLevel.None, usage, null, CancellationToken.None);

        _writerMock.Verify(
            w => w.WriteAsync(It.Is<SandboxStatusEvent>(e =>
                e.AttestationHash == null),
            CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task PlanCompleted_EmitsPlanCompletedEvent()
    {
        var planId = PlanId.New();
        var duration = TimeSpan.FromMinutes(2.5);

        await _sut.NotifyPlanCompletedAsync(planId, duration, CancellationToken.None);

        _writerMock.Verify(
            w => w.WriteAsync(It.Is<PlanCompletedEvent>(e =>
                e.PlanId == planId.Value.ToString() &&
                e.TotalDurationMs == 150000),
            CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task PlanFailed_EmitsPlanFailedEvent()
    {
        var planId = PlanId.New();
        var stepId = PlanStepId.New();

        await _sut.NotifyPlanFailedAsync(planId, stepId, "Connection timed out", CancellationToken.None);

        _writerMock.Verify(
            w => w.WriteAsync(It.Is<PlanFailedEvent>(e =>
                e.PlanId == planId.Value.ToString() &&
                e.FailedStepId == stepId.Value.ToString() &&
                e.ErrorMessage == "Connection timed out"),
            CancellationToken.None),
            Times.Once);
    }

    [Fact]
    public async Task NoWriter_SilentlyReturns()
    {
        _accessorMock.Setup(a => a.Writer).Returns((IAgUiEventWriter?)null);
        var sut = new AgUiPlanProgressNotifier(
            _accessorMock.Object,
            NullLogger<AgUiPlanProgressNotifier>.Instance);

        var planId = PlanId.New();

        await sut.NotifyPlanStartedAsync(planId, "Test", CreateMinimalGraph(planId, 1), CancellationToken.None);

        _writerMock.Verify(
            w => w.WriteAsync(It.IsAny<AgUiEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task WriterThrows_CatchesAndDoesNotThrow()
    {
        _writerMock
            .Setup(w => w.WriteAsync(It.IsAny<AgUiEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Stream closed"));

        var planId = PlanId.New();

        var act = () => _sut.NotifyPlanStartedAsync(planId, "Test", CreateMinimalGraph(planId, 1), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task OperationCanceledException_Propagates()
    {
        _writerMock
            .Setup(w => w.WriteAsync(It.IsAny<AgUiEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var planId = PlanId.New();

        var act = () => _sut.NotifyPlanStartedAsync(planId, "Test", CreateMinimalGraph(planId, 1), CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static PlanGraph CreateMinimalGraph(PlanId planId, int stepCount) => new()
    {
        Id = planId,
        Name = "Test Plan",
        Steps = Enumerable.Range(1, stepCount).Select(i => new PlanStep
        {
            Id = PlanStepId.New(),
            Name = $"Step {i}",
            Type = StepType.ToolUse,
            Configuration = new ToolUseConfig { ToolName = "test_tool" },
            RetryPolicy = new RetryPolicy(),
        }).ToList(),
        Edges = [],
        Configuration = new PlanConfiguration(),
    };
}
