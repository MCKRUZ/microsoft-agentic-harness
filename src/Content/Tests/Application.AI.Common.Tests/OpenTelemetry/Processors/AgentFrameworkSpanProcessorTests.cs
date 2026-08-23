using System.Diagnostics;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.OpenTelemetry.Instruments;
using Application.AI.Common.OpenTelemetry.Processors;
using Domain.AI.Governance;
using Domain.AI.Telemetry.Conventions;
using FluentAssertions;
using Moq;
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
    private readonly AgentFrameworkSpanProcessor _processor = new(TestSanitizer.Instance, TestRedactionFilter.Instance);

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

    [Fact]
    public void OnEnd_AgentFrameworkExecuteTool_ResultContainsSecret_RedactsBeforeCopying()
    {
        using var activity = _agentSource.StartActivity("test");
        activity.Should().NotBeNull();

        activity!.SetTag(ToolConventions.GenAiOperationName, ToolConventions.ExecuteToolOperation);
        activity.SetTag(ToolConventions.ToolCallResult, "result: ghp_abcdefghijklmnopqrstuvwxyz0123456789");

        _processor.OnEnd(activity);

        var eventContent = activity.GetTagItem("gen_ai.event.content") as string;
        eventContent.Should().NotBeNull();
        eventContent.Should().NotContain("ghp_abcdefghijklmnopqrstuvwxyz0123456789");
        eventContent.Should().Contain("[REDACTED:VendorApiKey]");
    }

    /// <summary>
    /// #470: this span used to redact without sanitizing first, so a secret split by
    /// invisible/zero-width characters (which the sanitizer canonicalizes away, but the redaction
    /// filter's anchored patterns do not) could dodge redaction here while the identical string was
    /// caught on the tool-failure-reporting path. Proven by ordering, not by depending on the real
    /// sanitizer's exact zero-width handling: a sanitizer mock that strips a marker only reveals it in
    /// the tag if redaction ran against the sanitizer's output, not the raw text.
    /// </summary>
    [Fact]
    public void OnEnd_SanitizesBeforeRedacting()
    {
        var sanitizer = new Mock<ICompositeResponseSanitizer>();
        sanitizer
            .Setup(s => s.Sanitize("secret is AKIA<split>ABCDEFGHIJ123456", It.IsAny<string?>()))
            .Returns(SanitizationResult.Clean("secret is AKIAABCDEFGHIJ123456"));
        var processor = new AgentFrameworkSpanProcessor(sanitizer.Object, TestRedactionFilter.Instance);

        using var activity = _agentSource.StartActivity("test");
        activity.Should().NotBeNull();
        activity!.SetTag(ToolConventions.GenAiOperationName, ToolConventions.ExecuteToolOperation);
        activity.SetTag(ToolConventions.ToolCallResult, "secret is AKIA<split>ABCDEFGHIJ123456");

        processor.OnEnd(activity);
        processor.Dispose();

        var eventContent = activity.GetTagItem("gen_ai.event.content") as string;
        eventContent.Should().NotBeNull();
        eventContent.Should().Contain("[REDACTED:AwsKey]",
            "redaction must run against the sanitizer's output, which joined the split key back together");
        eventContent.Should().NotContain("AKIAABCDEFGHIJ123456");
    }

    [Fact]
    public void Constructor_NullSanitizer_Throws()
    {
        var act = () => new AgentFrameworkSpanProcessor(null!, TestRedactionFilter.Instance);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullFilter_Throws()
    {
        var act = () => new AgentFrameworkSpanProcessor(TestSanitizer.Instance, null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
