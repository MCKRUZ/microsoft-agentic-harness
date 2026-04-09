using System.Diagnostics;
using Domain.AI.Telemetry.Conventions;
using FluentAssertions;
using Infrastructure.Observability.Processors;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.Observability.Tests.Processors;

public sealed class ToolEffectivenessProcessorTests : IDisposable
{
    private readonly ActivitySource _source = new("test.tool-effectiveness");
    private readonly ActivityListener _listener;

    public ToolEffectivenessProcessorTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _source.Dispose();
    }

    private static ToolEffectivenessProcessor CreateProcessor()
    {
        return new ToolEffectivenessProcessor(
            NullLogger<ToolEffectivenessProcessor>.Instance);
    }

    [Fact]
    public void OnEnd_ToolSpanWithResult_EnrichedWithEffectivenessAttributes()
    {
        var processor = CreateProcessor();
        using var activity = _source.StartActivity("execute-tool")!;
        activity.SetTag(ToolConventions.GenAiOperationName, ToolConventions.ExecuteToolOperation);
        activity.SetTag(ToolConventions.Name, "file_read");
        activity.SetTag(ToolConventions.ToolCallResult, "file contents here");

        processor.OnEnd(activity);

        activity.GetTagItem(ToolConventions.ResultEmpty).Should().Be(false);
        activity.GetTagItem(ToolConventions.ResultChars).Should().Be(18);
        activity.GetTagItem(ToolConventions.ResultTruncated).Should().Be(false);
    }

    [Fact]
    public void OnEnd_NonToolSpan_NotModified()
    {
        var processor = CreateProcessor();
        using var activity = _source.StartActivity("http-request")!;
        activity.SetTag(ToolConventions.GenAiOperationName, "chat");

        processor.OnEnd(activity);

        activity.GetTagItem(ToolConventions.ResultEmpty).Should().BeNull();
        activity.GetTagItem(ToolConventions.ResultChars).Should().BeNull();
        activity.GetTagItem(ToolConventions.ResultTruncated).Should().BeNull();
    }

    [Fact]
    public void OnEnd_SpanWithoutOperationName_NotModified()
    {
        var processor = CreateProcessor();
        using var activity = _source.StartActivity("plain-span")!;

        processor.OnEnd(activity);

        activity.GetTagItem(ToolConventions.ResultEmpty).Should().BeNull();
    }

    [Fact]
    public void OnEnd_EmptyToolResult_SetsEmptyFlag()
    {
        var processor = CreateProcessor();
        using var activity = _source.StartActivity("execute-tool")!;
        activity.SetTag(ToolConventions.GenAiOperationName, ToolConventions.ExecuteToolOperation);
        activity.SetTag(ToolConventions.Name, "web_search");
        activity.SetTag(ToolConventions.ToolCallResult, "");

        processor.OnEnd(activity);

        activity.GetTagItem(ToolConventions.ResultEmpty).Should().Be(true);
        activity.GetTagItem(ToolConventions.ResultChars).Should().Be(0);
    }

    [Fact]
    public void OnEnd_NullToolResult_SetsEmptyFlag()
    {
        var processor = CreateProcessor();
        using var activity = _source.StartActivity("execute-tool")!;
        activity.SetTag(ToolConventions.GenAiOperationName, ToolConventions.ExecuteToolOperation);
        activity.SetTag(ToolConventions.Name, "calculator");
        // No ToolCallResult tag set at all

        processor.OnEnd(activity);

        activity.GetTagItem(ToolConventions.ResultEmpty).Should().Be(true);
        activity.GetTagItem(ToolConventions.ResultChars).Should().Be(0);
    }

    [Fact]
    public void OnEnd_LargeToolResult_SetsTruncatedFlag()
    {
        var processor = CreateProcessor();
        using var activity = _source.StartActivity("execute-tool")!;
        activity.SetTag(ToolConventions.GenAiOperationName, ToolConventions.ExecuteToolOperation);
        activity.SetTag(ToolConventions.Name, "file_read");

        var largeResult = new string('x', ToolConventions.MaxResultLength + 1);
        activity.SetTag(ToolConventions.ToolCallResult, largeResult);

        processor.OnEnd(activity);

        activity.GetTagItem(ToolConventions.ResultTruncated).Should().Be(true);
        activity.GetTagItem(ToolConventions.ResultChars).Should().Be(ToolConventions.MaxResultLength + 1);
    }
}
