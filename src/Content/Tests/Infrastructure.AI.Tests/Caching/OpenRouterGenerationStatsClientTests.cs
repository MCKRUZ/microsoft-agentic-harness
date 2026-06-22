using System.Net;
using FluentAssertions;
using Infrastructure.AI.Caching;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Caching;

/// <summary>
/// Tests for <see cref="OpenRouterGenerationStatsClient"/> — the out-of-band fetcher that parses
/// OpenRouter's <c>GET /generation?id=</c> record and polls past the 404 the endpoint returns until
/// the record is written.
/// </summary>
public sealed class OpenRouterGenerationStatsClientTests
{
    private static readonly Uri s_baseAddress = new("https://openrouter.ai/api/v1/");

    private static OpenRouterGenerationStatsClient Create(StubHandler handler, int maxAttempts = 3)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = s_baseAddress };
        return new OpenRouterGenerationStatsClient(
            httpClient, NullLogger<OpenRouterGenerationStatsClient>.Instance, maxAttempts, TimeSpan.Zero);
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private const string FullRecord = """
    {"data":{
        "id":"gen-abc",
        "model":"anthropic/claude-sonnet-4.6",
        "native_tokens_prompt":18054,
        "native_tokens_cached":18041,
        "native_tokens_completion":120,
        "total_cost":0.0055,
        "cache_discount":0.0615
    }}
    """;

    [Fact]
    public async Task GetGenerationStats_FullRecord_ParsesAllFields()
    {
        var client = Create(new StubHandler(Json(FullRecord)));

        var stats = await client.GetGenerationStatsAsync("gen-abc");

        stats.Should().NotBeNull();
        stats!.Model.Should().Be("anthropic/claude-sonnet-4.6");
        stats.CacheReadTokens.Should().Be(18041);
        stats.PromptTokens.Should().Be(18054);
        stats.TotalCost.Should().Be(0.0055m);
        stats.CacheDiscount.Should().Be(0.0615m);
    }

    [Fact]
    public async Task GetGenerationStats_404ThenOk_RetriesAndReturnsRecord()
    {
        var handler = new StubHandler(
            new HttpResponseMessage(HttpStatusCode.NotFound),
            new HttpResponseMessage(HttpStatusCode.NotFound),
            Json(FullRecord));
        var client = Create(handler, maxAttempts: 3);

        var stats = await client.GetGenerationStatsAsync("gen-abc");

        stats.Should().NotBeNull();
        handler.CallCount.Should().Be(3, "the client should poll past the two 404s");
    }

    [Fact]
    public async Task GetGenerationStats_PersistentlyNotFound_ReturnsNullAfterMaxAttempts()
    {
        var handler = new StubHandler(); // always 404
        var client = Create(handler, maxAttempts: 4);

        var stats = await client.GetGenerationStatsAsync("gen-missing");

        stats.Should().BeNull();
        handler.CallCount.Should().Be(4, "it should stop after the configured attempt cap");
    }

    [Fact]
    public async Task GetGenerationStats_EmptyId_ReturnsNullWithoutHttpCall()
    {
        var handler = new StubHandler(Json(FullRecord));
        var client = Create(handler);

        var stats = await client.GetGenerationStatsAsync("   ");

        stats.Should().BeNull();
        handler.CallCount.Should().Be(0, "an empty id should short-circuit before any request");
    }

    [Fact]
    public async Task GetGenerationStats_ServerError_ReturnsNullWithoutRetry()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = Create(handler, maxAttempts: 3);

        var stats = await client.GetGenerationStatsAsync("gen-abc");

        stats.Should().BeNull();
        handler.CallCount.Should().Be(1, "a non-404 failure is terminal — only 404 means 'retry'");
    }

    [Fact]
    public async Task GetGenerationStats_MissingDataEnvelope_ReturnsNull()
    {
        var client = Create(new StubHandler(Json("""{"error":"nope"}""")));

        var stats = await client.GetGenerationStatsAsync("gen-abc");

        stats.Should().BeNull();
    }

    [Fact]
    public async Task GetGenerationStats_MalformedJson_ReturnsNull()
    {
        var client = Create(new StubHandler(Json("not json at all")));

        var stats = await client.GetGenerationStatsAsync("gen-abc");

        stats.Should().BeNull();
    }

    [Fact]
    public async Task GetGenerationStats_NullCacheField_DefaultsToZero()
    {
        // A generation that never touched the cache omits / nulls the cache fields.
        const string noCache = """
        {"data":{"model":"anthropic/claude-sonnet-4.6","native_tokens_prompt":500,"total_cost":0.01}}
        """;
        var client = Create(new StubHandler(Json(noCache)));

        var stats = await client.GetGenerationStatsAsync("gen-abc");

        stats.Should().NotBeNull();
        stats!.CacheReadTokens.Should().Be(0);
        stats.CacheDiscount.Should().Be(0m);
        stats.PromptTokens.Should().Be(500);
    }

    [Fact]
    public async Task GetGenerationStats_RequestUsesIdQueryParameter()
    {
        var handler = new StubHandler(Json(FullRecord));
        var client = Create(handler);

        await client.GetGenerationStatsAsync("gen abc/123");

        handler.LastRequestUri.Should().NotBeNull();
        handler.LastRequestUri!.AbsolutePath.Should().EndWith("/generation");
        // The id is URL-escaped into the query string.
        handler.LastRequestUri.Query.Should().Contain("id=gen%20abc%2F123");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public StubHandler(params HttpResponseMessage[] responses)
            => _responses = new Queue<HttpResponseMessage>(responses);

        public int CallCount { get; private set; }
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestUri = request.RequestUri;
            var response = _responses.Count > 0
                ? _responses.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.NotFound);
            return Task.FromResult(response);
        }
    }
}
