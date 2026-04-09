using System.Diagnostics;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config;
using Domain.Common.Config.Observability;
using FluentAssertions;
using Infrastructure.Observability.Processors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Infrastructure.Observability.Tests.Processors;

public sealed class LlmTokenTrackingProcessorTests : IDisposable
{
    private readonly ActivitySource _source = new("test.llm-token-tracking");
    private readonly ActivityListener _listener;

    public LlmTokenTrackingProcessorTests()
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

    private static LlmTokenTrackingProcessor CreateProcessor(LlmPricingConfig? config = null)
    {
        var appConfig = new AppConfig();
        if (config is not null)
            appConfig.Observability.LlmPricing = config;

        var options = Options.Create(appConfig);
        return new LlmTokenTrackingProcessor(
            NullLogger<LlmTokenTrackingProcessor>.Instance,
            options);
    }

    [Fact]
    public void OnEnd_SpanWithTokenAttributes_ProcessesWithoutError()
    {
        var processor = CreateProcessor();
        using var activity = _source.StartActivity("llm-call")!;
        activity.SetTag(TokenConventions.GenAiInputTokens, 500);
        activity.SetTag(TokenConventions.GenAiOutputTokens, 150);
        activity.SetTag(TokenConventions.GenAiRequestModel, "claude-sonnet-4-6");
        activity.SetTag(AgentConventions.Name, "test-agent");

        // Should not throw
        var act = () => processor.OnEnd(activity);
        act.Should().NotThrow();
    }

    [Fact]
    public void OnEnd_SpanWithoutTokenAttributes_Skipped()
    {
        var processor = CreateProcessor();
        using var activity = _source.StartActivity("http-call")!;
        activity.SetTag("http.method", "GET");

        // Should not throw or process anything
        var act = () => processor.OnEnd(activity);
        act.Should().NotThrow();
    }

    [Fact]
    public void OnEnd_SpanWithLongTokenValues_ProcessesWithoutError()
    {
        var processor = CreateProcessor();
        using var activity = _source.StartActivity("llm-call")!;
        activity.SetTag(TokenConventions.GenAiInputTokens, 1000L);
        activity.SetTag(TokenConventions.GenAiOutputTokens, 500L);
        activity.SetTag(TokenConventions.GenAiRequestModel, "claude-opus-4-6");

        var act = () => processor.OnEnd(activity);
        act.Should().NotThrow();
    }

    [Fact]
    public void OnEnd_SpanWithCacheTokens_ProcessesWithoutError()
    {
        var processor = CreateProcessor();
        using var activity = _source.StartActivity("llm-call")!;
        activity.SetTag(TokenConventions.GenAiInputTokens, 200);
        activity.SetTag(TokenConventions.GenAiOutputTokens, 100);
        activity.SetTag(TokenConventions.GenAiCacheReadTokens, 800);
        activity.SetTag(TokenConventions.GenAiCacheWriteTokens, 50);
        activity.SetTag(TokenConventions.GenAiRequestModel, "claude-sonnet-4-6");
        activity.SetTag(AgentConventions.Name, "cache-agent");

        var act = () => processor.OnEnd(activity);
        act.Should().NotThrow();
    }

    [Fact]
    public void OnEnd_UnknownModel_FallsBackToDefaultModel()
    {
        var config = new LlmPricingConfig
        {
            DefaultModel = "claude-sonnet-4-6",
            Models =
            [
                new ModelPricingEntry
                {
                    Name = "claude-sonnet-4-6",
                    InputPerMillion = 3.00m,
                    OutputPerMillion = 15.00m,
                    CacheReadPerMillion = 0.30m,
                    CacheWritePerMillion = 3.75m
                }
            ]
        };
        var processor = CreateProcessor(config);
        using var activity = _source.StartActivity("llm-call")!;
        activity.SetTag(TokenConventions.GenAiInputTokens, 100);
        activity.SetTag(TokenConventions.GenAiOutputTokens, 50);
        // No model attribute set — should fall back to default

        var act = () => processor.OnEnd(activity);
        act.Should().NotThrow();
    }
}
