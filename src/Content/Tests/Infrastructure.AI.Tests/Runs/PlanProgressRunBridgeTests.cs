using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Bundles;
using Domain.AI.Planner;
using Domain.AI.Runs;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Runs;
using Infrastructure.AI.Tests.Runs.Support;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Runs;

/// <summary>
/// What a workflow's progress feed carries (#1005). The run progress broker is exempt from the personal-data inventory on
/// the ground that its events say how far a run has got and never what the work produced, so a step's output never reaches
/// the feed: it belongs to the run's result, which the declared stores hold, erase and export.
/// </summary>
public sealed class PlanProgressRunBridgeTests
{
    private const string Output = "the step's output, which may quote the person's own words";

    private readonly PlanId _plan = PlanId.New();
    private readonly InMemoryRunProgressBroker _broker = new(
        new StaticOptionsMonitor<AppConfig>(new AppConfig()), TimeProvider.System);
    private readonly PlanProgressRunBridge _sut;

    public PlanProgressRunBridgeTests()
    {
        var runs = new Mock<IRunJobStore>();
        runs.Setup(r => r.FindLiveRunForTarget(RunKind.Workflow, _plan.Value.ToString())).Returns(new RunRecord
        {
            JobId = "job-1",
            Kind = RunKind.Workflow,
            TargetId = _plan.Value.ToString(),
            OwnerId = "owner-1",
            Envelope = new CapabilityEnvelope(),
            Status = RunStatus.Running,
            CreatedAt = DateTimeOffset.UnixEpoch,
        });
        _sut = new PlanProgressRunBridge(runs.Object, _broker);
    }

    [Fact]
    public async Task NotifyStepCompletedAsync_WithAnOutputSummary_PublishesTheStatusButNeverTheOutput()
    {
        using var watcher = _broker.Subscribe("job-1", "owner-1", tenantId: null)!;

        await _sut.NotifyStepCompletedAsync(
            _plan, PlanStepId.New(), StepExecutionStatus.Completed, TimeSpan.FromSeconds(1), Output, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var evt = await watcher.ReadAllAsync(cts.Token).FirstAsync(cts.Token);
        evt.Kind.Should().Be(RunProgressKind.StepCompleted);
        evt.Status.Should().Be(nameof(StepExecutionStatus.Completed));
        new[] { evt.StepId, evt.StepName, evt.Status, evt.Detail }
            .Should().NotContain(field => field != null && field.Contains("person's own words"),
                "no part of a step's output may reach the feed, whole or quoted inside another field");
    }

    [Fact]
    public async Task NotifyStepStartedAsync_PublishesTheStepAndItsKind_ButNoArguments()
    {
        using var watcher = _broker.Subscribe("job-1", "owner-1", tenantId: null)!;

        await _sut.NotifyStepStartedAsync(_plan, PlanStepId.New(), "summarise", StepType.LlmCall, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var evt = await watcher.ReadAllAsync(cts.Token).FirstAsync(cts.Token);
        evt.StepName.Should().Be("summarise", "a step's name comes from the stored workflow, which the plan store holds");
        evt.Detail.Should().Be(nameof(StepType.LlmCall));
    }
}
