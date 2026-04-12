using System.Diagnostics;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.MetaHarness;
using FluentAssertions;
using Infrastructure.Observability.Processors;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.Observability.Tests.Processors;

public sealed class CausalSpanAttributionProcessorTests : IDisposable
{
    private readonly ActivitySource _source = new("test.causal-attribution");
    private readonly ActivityListener _listener;

    public CausalSpanAttributionProcessorTests()
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

    private static CausalSpanAttributionProcessor CreateProcessor() =>
        new(NullLogger<CausalSpanAttributionProcessor>.Instance);

    [Fact]
    public void OnEnd_ToolCallSpan_AddsToolNameTag()
    {
        var processor = CreateProcessor();
        using var activity = _source.StartActivity("execute-tool")!;
        activity.SetTag(ToolConventions.GenAiOperationName, ToolConventions.ExecuteToolOperation);
        activity.SetTag(ToolConventions.Name, "file_read");

        processor.OnEnd(activity);

        activity.GetTagItem(ToolConventions.GenAiToolName).Should().Be("file_read");
    }

    [Fact]
    public void OnEnd_ToolCallSpan_AddsInputHashTag()
    {
        var processor = CreateProcessor();
        using var activity = _source.StartActivity("execute-tool")!;
        activity.SetTag(ToolConventions.GenAiOperationName, ToolConventions.ExecuteToolOperation);
        activity.SetTag(ToolConventions.Name, "file_read");
        activity.SetTag(ToolConventions.ToolCallArguments, "{\"path\": \"/tmp/test.txt\"}");

        processor.OnEnd(activity);

        var hash = activity.GetTagItem(ToolConventions.InputHash) as string;
        hash.Should().NotBeNullOrEmpty();
        hash.Should().MatchRegex("^[0-9a-f]{64}$", "input hash must be a lowercase 64-char SHA-256 hex string");
    }

    [Fact]
    public void OnEnd_ToolCallSpan_AddsResultCategoryTag_Success()
    {
        var processor = CreateProcessor();
        using var activity = _source.StartActivity("execute-tool")!;
        activity.SetTag(ToolConventions.GenAiOperationName, ToolConventions.ExecuteToolOperation);
        activity.SetStatus(ActivityStatusCode.Ok);

        processor.OnEnd(activity);

        activity.GetTagItem(ToolConventions.ResultCategory).Should().Be(TraceResultCategories.Success);
    }

    [Fact]
    public void OnEnd_ToolCallSpan_AddsResultCategoryTag_Error()
    {
        var processor = CreateProcessor();
        using var activity = _source.StartActivity("execute-tool")!;
        activity.SetTag(ToolConventions.GenAiOperationName, ToolConventions.ExecuteToolOperation);
        activity.SetStatus(ActivityStatusCode.Error);

        processor.OnEnd(activity);

        activity.GetTagItem(ToolConventions.ResultCategory).Should().Be(TraceResultCategories.Error);
    }

    [Fact]
    public void OnEnd_WhenCandidateIdOnContext_AddsCandidateIdTag()
    {
        var processor = CreateProcessor();
        using var activity = _source.StartActivity("execute-tool")!;
        activity.SetTag(ToolConventions.GenAiOperationName, ToolConventions.ExecuteToolOperation);
        activity.AddBaggage(ToolConventions.HarnessCandidateId, "cand-abc-123");

        processor.OnEnd(activity);

        activity.GetTagItem(ToolConventions.HarnessCandidateId).Should().Be("cand-abc-123");
    }

    [Fact]
    public void OnEnd_WhenNoCandidateId_DoesNotAddCandidateIdTag()
    {
        var processor = CreateProcessor();
        using var activity = _source.StartActivity("execute-tool")!;
        activity.SetTag(ToolConventions.GenAiOperationName, ToolConventions.ExecuteToolOperation);
        // No candidate baggage

        processor.OnEnd(activity);

        activity.GetTagItem(ToolConventions.HarnessCandidateId).Should().BeNull();
    }

    [Fact]
    public void OnEnd_InputHashComputation_IsNotPerformedWhenIsAllDataRequestedFalse()
    {
        // A listener returning PropagationData means IsAllDataRequested = false;
        // tags set on the activity are no-ops, so gen_ai.operation.name won't be stored.
        // The processor must NOT compute hashes when IsAllDataRequested = false.
        using var lowDataSource = new ActivitySource("test.causal-attribution.lowdata");
        using var lowDataListener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.PropagationData
        };
        ActivitySource.AddActivityListener(lowDataListener);

        var processor = CreateProcessor();
        using var activity = lowDataSource.StartActivity("execute-tool")!;

        processor.OnEnd(activity);

        // IsAllDataRequested = false → no hash computed → tag absent
        activity.GetTagItem(ToolConventions.InputHash).Should().BeNull();
    }

    [Fact]
    public void OnEnd_NonToolSpan_DoesNotAddCausalAttributes()
    {
        var processor = CreateProcessor();
        using var activity = _source.StartActivity("chat-completion")!;
        activity.SetTag(ToolConventions.GenAiOperationName, "chat");

        processor.OnEnd(activity);

        activity.GetTagItem(ToolConventions.GenAiToolName).Should().BeNull();
        activity.GetTagItem(ToolConventions.InputHash).Should().BeNull();
        activity.GetTagItem(ToolConventions.ResultCategory).Should().BeNull();
    }

    [Fact]
    public void OnEnd_SpanWithNoOperationName_DoesNotAddCausalAttributes()
    {
        var processor = CreateProcessor();
        using var activity = _source.StartActivity("plain-span")!;
        // No gen_ai.operation.name tag at all

        processor.OnEnd(activity);

        activity.GetTagItem(ToolConventions.GenAiToolName).Should().BeNull();
        activity.GetTagItem(ToolConventions.ResultCategory).Should().BeNull();
    }
}
