using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Middleware;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config.Observability;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Application.AI.Common.Tests.Middleware;

/// <summary>
/// Tests for <see cref="CacheStatsEnrichingChatClient"/> — the middleware that captures the
/// provider response id, fetches the out-of-band generation stats, and emits the cache metrics.
/// </summary>
public sealed class CacheStatsEnrichingChatClientTests
{
    private const string Model = "anthropic/claude-sonnet-4.6";

    [Fact]
    public async Task GetResponseAsync_WithStats_EmitsCacheReadAndSavingsTaggedByAgent()
    {
        var agent = $"agent-{Guid.NewGuid():N}";
        var stats = new GenerationStats(Model, CacheReadTokens: 18041, PromptTokens: 18054,
            TotalCost: 0.0055m, CacheDiscount: 0.0615m);
        var statsClient = new FakeStatsClient(stats);
        var inner = new TestChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "hi"))
        {
            ResponseId = "gen-1",
            ModelId = Model
        });
        var sut = Create(inner, statsClient, agent);

        var cacheReads = CaptureCounterLong(TokenConventions.CacheRead, agent);
        var savings = CaptureCounterDouble(TokenConventions.CostCacheSavings, agent);
        var actualCost = CaptureCounterDouble(TokenConventions.CostActual, agent);

        var response = await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);
        await sut.EnrichmentCompletion; // deterministically await the background fetch

        response.ResponseId.Should().Be("gen-1");
        statsClient.LastId.Should().Be("gen-1");
        cacheReads.Sum().Should().Be(18041);
        savings.Sum().Should().BeApproximately(0.0615, 0.0001);
        actualCost.Sum().Should().BeApproximately(0.0055, 0.0001,
            "the authoritative provider cost is emitted as cost_actual");
    }

    [Fact]
    public async Task GetResponseAsync_TagsCacheMetricsWithShortModelLabel()
    {
        // The dashboards group cache/cost by the short `model` label (sum by (model)). Emitting the
        // dotted gen_ai.request.model key here would leave these metrics — including cost_actual,
        // the preferred cost source — invisible to those tiles, and disagree with
        // LlmTokenTrackingProcessor, which emits the same instruments. See TokenConventions.Model.
        var agent = $"agent-{Guid.NewGuid():N}";
        var stats = new GenerationStats(Model, CacheReadTokens: 100, PromptTokens: 200,
            TotalCost: 0.01m, CacheDiscount: 0.02m);
        var statsClient = new FakeStatsClient(stats);
        var inner = new TestChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "hi"))
        {
            ResponseId = "gen-model",
            ModelId = Model
        });
        var sut = Create(inner, statsClient, agent);

        var models = CaptureModelTag(TokenConventions.CacheRead, agent);

        await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);
        await sut.EnrichmentCompletion;

        models.Should().ContainSingle(
            "the cache metric must carry the short `model` label, not the dotted gen_ai.request.model key")
            .Which.Should().Be(Model);
    }

    [Fact]
    public async Task GetResponseAsync_NoResponseId_DoesNotCallStatsClient()
    {
        var statsClient = new FakeStatsClient(null);
        var inner = new TestChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "hi")));
        var sut = Create(inner, statsClient, "agent");

        await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);
        await sut.EnrichmentCompletion;

        statsClient.Calls.Should().Be(0, "without a response id there is nothing to look up");
    }

    [Fact]
    public async Task GetResponseAsync_StatsUnavailable_EmitsNoCacheMetrics()
    {
        var agent = $"agent-{Guid.NewGuid():N}";
        var statsClient = new FakeStatsClient(null); // record never became available
        var inner = new TestChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "hi"))
        {
            ResponseId = "gen-2"
        });
        var sut = Create(inner, statsClient, agent);

        var cacheReads = CaptureCounterLong(TokenConventions.CacheRead, agent);

        await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);
        await sut.EnrichmentCompletion;

        statsClient.Calls.Should().Be(1);
        cacheReads.Should().BeEmpty("cache instruments only come from a fetched generation record");
    }

    [Fact]
    public async Task GetResponseAsync_StatsUnavailable_EmitsEstimatedCostAsActualCost()
    {
        // When the generation record can't be fetched, cost_actual must still be emitted from the
        // per-call token estimate so the cost tiles never silently drop a call's spend.
        var agent = $"agent-{Guid.NewGuid():N}";
        var statsClient = new FakeStatsClient(null);
        var inner = new TestChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "hi"))
        {
            ResponseId = "gen-fallback",
            ModelId = DefaultModel,
            Usage = new UsageDetails { InputTokenCount = 1_000_000, OutputTokenCount = 0 }
        });
        var sut = Create(inner, statsClient, agent);

        var actualCost = CaptureCounterDouble(TokenConventions.CostActual, agent);

        await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);
        await sut.EnrichmentCompletion;

        statsClient.Calls.Should().Be(1);
        actualCost.Sum().Should().BeApproximately(3.00, 0.0001,
            "1,000,000 input tokens at the $3.00/M rate, estimated because the real record was unavailable");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_CapturesResponseIdAndEmits()
    {
        var agent = $"agent-{Guid.NewGuid():N}";
        var stats = new GenerationStats(Model, CacheReadTokens: 1000, PromptTokens: 2000,
            TotalCost: 0.01m, CacheDiscount: 0.02m);
        var statsClient = new FakeStatsClient(stats);
        var updates = new[]
        {
            new ChatResponseUpdate { Contents = [new TextContent("par")] },
            new ChatResponseUpdate { ResponseId = "gen-stream", ModelId = Model, Contents = [new TextContent("tial")] }
        };
        var inner = new TestChatClient(updates);
        var sut = Create(inner, statsClient, agent);

        var cacheReads = CaptureCounterLong(TokenConventions.CacheRead, agent);

        await foreach (var _ in sut.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
        {
            // drain the stream
        }
        await sut.EnrichmentCompletion;

        statsClient.LastId.Should().Be("gen-stream");
        cacheReads.Sum().Should().Be(1000);
    }

    [Fact]
    public async Task GetResponseAsync_ZeroCacheRead_DoesNotEmitCacheReadCounter()
    {
        var agent = $"agent-{Guid.NewGuid():N}";
        var stats = new GenerationStats(Model, CacheReadTokens: 0, PromptTokens: 500,
            TotalCost: 0.01m, CacheDiscount: 0m);
        var statsClient = new FakeStatsClient(stats);
        var inner = new TestChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "hi"))
        {
            ResponseId = "gen-3",
            ModelId = Model
        });
        var sut = Create(inner, statsClient, agent);

        var cacheReads = CaptureCounterLong(TokenConventions.CacheRead, agent);

        await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);
        await sut.EnrichmentCompletion;

        cacheReads.Should().BeEmpty("a cache miss should not record a zero cache-read measurement");
    }

    private const string DefaultModel = "claude-sonnet-4-6";

    private static readonly IReadOnlyDictionary<string, ModelPricingEntry> Pricing =
        new Dictionary<string, ModelPricingEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-sonnet-4-6"] = new ModelPricingEntry
            {
                Name = "claude-sonnet-4-6",
                InputPerMillion = 3.00m,
                OutputPerMillion = 15.00m,
                CacheReadPerMillion = 0.30m,
                CacheWritePerMillion = 3.75m
            }
        };

    private static CacheStatsEnrichingChatClient Create(
        IChatClient inner, IGenerationStatsClient statsClient, string agentName)
        => new(inner, statsClient, agentName, Pricing, DefaultModel,
            NullLogger<CacheStatsEnrichingChatClient>.Instance);

    /// <summary>Captures long-counter measurements emitted for a specific agent.</summary>
    private static List<long> CaptureCounterLong(string instrumentName, string agent)
    {
        var values = new List<long>();
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == instrumentName)
                    l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            if (HasAgentTag(tags, agent))
                values.Add(value);
        });
        listener.Start();
        return values;
    }

    /// <summary>Captures double-counter measurements emitted for a specific agent.</summary>
    private static List<double> CaptureCounterDouble(string instrumentName, string agent)
    {
        var values = new List<double>();
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == instrumentName)
                    l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            if (HasAgentTag(tags, agent))
                values.Add(value);
        });
        listener.Start();
        return values;
    }

    /// <summary>Captures the value of the short <c>model</c> tag on long-counter measurements for an agent.</summary>
    private static List<string> CaptureModelTag(string instrumentName, string agent)
    {
        var models = new List<string>();
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == instrumentName)
                    l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            if (!HasAgentTag(tags, agent))
                return;
            foreach (var tag in tags)
                if (tag.Key == TokenConventions.Model && tag.Value is string model)
                    models.Add(model);
        });
        listener.Start();
        return models;
    }

    private static bool HasAgentTag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string agent)
    {
        foreach (var tag in tags)
            if (tag.Key == AgentConventions.Name && tag.Value as string == agent)
                return true;
        return false;
    }

    private sealed class FakeStatsClient : IGenerationStatsClient
    {
        private readonly GenerationStats? _stats;

        public FakeStatsClient(GenerationStats? stats) => _stats = stats;

        public int Calls { get; private set; }
        public string? LastId { get; private set; }

        public Task<GenerationStats?> GetGenerationStatsAsync(
            string generationId, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastId = generationId;
            return Task.FromResult(_stats);
        }
    }

    private sealed class TestChatClient : IChatClient
    {
        private readonly ChatResponse? _response;
        private readonly IReadOnlyList<ChatResponseUpdate>? _updates;

        public TestChatClient(ChatResponse response) => _response = response;
        public TestChatClient(IReadOnlyList<ChatResponseUpdate> updates) => _updates = updates;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_response!);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in _updates!)
                yield return update;
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
