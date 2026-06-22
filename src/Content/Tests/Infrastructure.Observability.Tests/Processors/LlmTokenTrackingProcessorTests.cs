using System.Diagnostics;
using System.Diagnostics.Metrics;
using Application.AI.Common.Interfaces;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config;
using Domain.Common.Config.Observability;
using FluentAssertions;
using Infrastructure.Observability.Processors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
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
            options,
            Mock.Of<IBudgetTrackingService>());
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

    /// <summary>
    /// Regression guard for the cache-hit-rate histogram: on the OpenRouter path the cache counts
    /// never reach the span (they arrive out-of-band via CacheStatsEnrichingChatClient). A span with
    /// input/output tokens but NO cache attributes must NOT record a hit rate, otherwise every such
    /// call would push a 0 into the histogram and halve the true average.
    /// </summary>
    [Fact]
    public void OnEnd_SpanWithoutCacheAttributes_DoesNotRecordHitRate()
    {
        var processor = CreateProcessor();

        var hitRates = new List<double>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == TokenConventions.CacheHitRate)
                    l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == TokenConventions.GenAiRequestModel && tag.Value as string == "no-cache-attrs-model")
                    hitRates.Add(value);
        });
        listener.Start();

        using (var activity = _source.StartActivity("llm-call")!)
        {
            activity.SetTag(TokenConventions.GenAiInputTokens, 1000);
            activity.SetTag(TokenConventions.GenAiOutputTokens, 200);
            activity.SetTag(TokenConventions.GenAiRequestModel, "no-cache-attrs-model");
            // No cache_read / cache_creation attributes — the OpenRouter shape.
            processor.OnEnd(activity);
        }

        hitRates.Should().BeEmpty(
            "a span without cache attributes must not pollute the hit-rate histogram with a 0");
    }

    /// <summary>
    /// The native path (e.g. Anthropic/Foundry) DOES carry cache attributes on the span, so the
    /// processor must still record the hit rate there.
    /// </summary>
    [Fact]
    public void OnEnd_SpanWithCacheAttributes_RecordsHitRate()
    {
        var processor = CreateProcessor();

        var hitRates = new List<double>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == TokenConventions.CacheHitRate)
                    l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == TokenConventions.GenAiRequestModel && tag.Value as string == "with-cache-attrs-model")
                    hitRates.Add(value);
        });
        listener.Start();

        using (var activity = _source.StartActivity("llm-call")!)
        {
            activity.SetTag(TokenConventions.GenAiInputTokens, 200);
            activity.SetTag(TokenConventions.GenAiOutputTokens, 100);
            activity.SetTag(TokenConventions.GenAiCacheReadTokens, 800);
            activity.SetTag(TokenConventions.GenAiRequestModel, "with-cache-attrs-model");
            processor.OnEnd(activity);
        }

        hitRates.Should().ContainSingle()
            .Which.Should().BeApproximately(0.8, 0.0001, "800 cache-read of 1000 total input");
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

    /// <summary>
    /// Regression guard for the empty Prometheus cost tiles: a span carrying a non-null model
    /// that is NOT in the pricing table (e.g. a deployment alias) must still emit cost, priced
    /// at the configured default model. Before the fix, only a *null* model fell back to the
    /// default, so an unpriced non-null model skipped cost entirely.
    /// </summary>
    [Fact]
    public void OnEnd_NonNullUnpricedModel_StillEmitsCostViaDefaultPricing()
    {
        const string uniqueAgent = "cost-fallback-guard-agent";
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

        var costs = new List<double>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == TokenConventions.CostEstimated)
                    l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            // Filter by our unique agent so concurrent tests recording the same
            // process-global instrument can't leak measurements into this assertion.
            foreach (var tag in tags)
                if (tag.Key == AgentConventions.Name && tag.Value as string == uniqueAgent)
                    costs.Add(value);
        });
        listener.Start();

        using (var activity = _source.StartActivity("llm-call")!)
        {
            activity.SetTag(TokenConventions.GenAiInputTokens, 1_000_000);
            activity.SetTag(TokenConventions.GenAiOutputTokens, 0);
            activity.SetTag(TokenConventions.GenAiRequestModel, "gpt-4o"); // non-null, NOT in the pricing table
            activity.SetTag(AgentConventions.Name, uniqueAgent);
            processor.OnEnd(activity);
        }

        costs.Should().NotBeEmpty(
            "a non-null unpriced model must still emit cost via the default-model pricing fallback");
        costs.Sum().Should().BeApproximately(3.00, 0.0001,
            "1,000,000 input tokens priced at the default model's $3.00/M rate");
    }
}
