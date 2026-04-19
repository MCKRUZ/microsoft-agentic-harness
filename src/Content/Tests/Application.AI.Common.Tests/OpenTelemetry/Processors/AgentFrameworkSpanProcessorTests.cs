using System.Diagnostics;
using Application.AI.Common.OpenTelemetry.Instruments;
using Application.AI.Common.OpenTelemetry.Processors;
using Domain.AI.Telemetry.Conventions;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.OpenTelemetry.Processors;

/// <summary>
/// Tests for <see cref="AgentFrameworkSpanProcessor"/> covering span enrichment
/// for execute_tool operations and skipping non-matching activities.
/// </summary>
public class AgentFrameworkSpanProcessorTests : IDisposable
{
    private readonly ActivitySource _agentSource = new(AiSourceNames.AgentFrameworkExact);
    private readonly ActivitySource _otherSource = new("SomeOther.Source");
    private readonly ActivityListener _listener;
    private readonly AgentFrameworkSpanProcessor _processor = new();

    public AgentFrameworkSpanProcessorTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _agentSource.Dispose();
        _otherSource.Dispose();
        _processor.Dispose();
    }

    [Fact]
    public void OnEnd_AgentFrameworkExecuteTool_WithResult_SetsEventContentTag()
    {
        using var activity = _agentSource.StartActivity("test");
        activity.Should().NotBeNull();

        activity!.SetTag(ToolConventions.GenAiOperationName, ToolConventions.ExecuteToolOperation);
        activity.SetTag(ToolConventions.ToolCallResult, "tool output data");

        _processor.OnEnd(activity);

        var eventContent = activity.GetTagItem("gen_ai.event.content") as string;
        eventContent.Should().Be("tool output data");
    }

    [Fact]
    public void OnEnd_AgentFrameworkExecuteTool_LongResult_Truncates()
    {
        using var activity = _agentSource.StartActivity("test");
        activity.Should().NotBeNull();

        var longResult = new string('x', ToolConventions.MaxResultLength + 500);
        activity!.SetTag(ToolConventions.GenAiOperationName, ToolConventions.ExecuteToolOperation);
        activity.SetTag(ToolConventions.ToolCallResult, longResult);

        _processor.OnEnd(activity);

        var eventContent = activity.GetTagItem("gen_ai.event.content") as string;
        eventContent.Should().NotBeNull();
        eventContent!.Should().EndWith("...[truncated]");
        eventContent.Length.Should().BeLessThan(longResult.Length);
    }

    [Fact]
    public void OnEnd_AgentFrameworkExecuteTool_NullResult_DoesNotSetTag()
    {
        using var activity = _agentSource.StartActivity("test");
        activity.Should().NotBeNull();

        activity!.SetTag(ToolConventions.GenAiOperationName, ToolConventions.ExecuteToolOperation);

        _processor.OnEnd(activity);

        var eventContent = activity.GetTagItem("gen_ai.event.content");
        eventContent.Should().BeNull();
    }

    [Fact]
    public void OnEnd_DifferentSource_DoesNothing()
    {
        using var activity = _otherSource.StartActivity("test");
        activity.Should().NotBeNull();

        activity!.SetTag(ToolConventions.GenAiOperationName, ToolConventions.ExecuteToolOperation);
        activity.SetTag(ToolConventions.ToolCallResult, "some result");

        _processor.OnEnd(activity);

        var eventContent = activity.GetTagItem("gen_ai.event.content");
        eventContent.Should().BeNull();
    }

    [Fact]
    public void OnEnd_NonExecuteToolOperation_DoesNothing()
    {
        using var activity = _agentSource.StartActivity("test");
        activity.Should().NotBeNull();

        activity!.SetTag(ToolConventions.GenAiOperationName, "chat");
        activity.SetTag(ToolConventions.ToolCallResult, "some result");

        _processor.OnEnd(activity);

        var eventContent = activity.GetTagItem("gen_ai.event.content");
        eventContent.Should().BeNull();
    }
}
