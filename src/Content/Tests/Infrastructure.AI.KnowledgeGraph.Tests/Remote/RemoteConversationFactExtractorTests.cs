using System.Net;
using System.Text;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.Remote;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Remote;

/// <summary>
/// Tests for <see cref="RemoteConversationFactExtractor"/> against a stubbed avatar
/// <c>/extract</c> endpoint. Pins the fail-safe contract every <c>IConversationFactExtractor</c>
/// must honor: an HTTP failure, an empty/malformed body, or a thrown exception all degrade to "no
/// facts extracted this turn" rather than propagating to the caller.
/// </summary>
public sealed class RemoteConversationFactExtractorTests
{
    private static RemoteConversationFactExtractor CreateSut(StubHandler handler) =>
        new(new StubHttpClientFactory(handler), NullLogger<RemoteConversationFactExtractor>.Instance);

    [Fact]
    public async Task ExtractAsync_SuccessResponse_MapsToConversationFacts()
    {
        var handler = new StubHandler(_ => Ok("""
            [
              { "key": "favorite-color", "content": "User likes blue", "entityType": "Fact", "confidence": 0.9 }
            ]
            """));
        var sut = CreateSut(handler);

        var facts = await sut.ExtractAsync("what's your favorite color?", "I like blue", "conv-1", 3);

        facts.Should().ContainSingle();
        facts[0].Key.Should().Be("favorite-color");
        facts[0].Content.Should().Be("User likes blue");
        facts[0].EntityType.Should().Be("Fact");
        facts[0].Confidence.Should().Be(0.9);
    }

    [Fact]
    public async Task ExtractAsync_PostsToExtractWithExpectedBody()
    {
        var handler = new StubHandler(_ => Ok("[]"));
        var sut = CreateSut(handler);

        await sut.ExtractAsync("hello", "hi there", "conv-42", 7);

        handler.LastRequestUri!.AbsolutePath.Should().EndWith("/extract");
        handler.LastRequestBody.Should().Contain("\"threadId\":\"conv-42\"");
        handler.LastRequestBody.Should().Contain("\"turnNumber\":7");
    }

    [Fact]
    public async Task ExtractAsync_EntriesMissingKeyOrContent_AreDropped()
    {
        var handler = new StubHandler(_ => Ok("""
            [
              { "key": null, "content": "orphan content" },
              { "key": "real-key", "content": "" },
              { "key": "good-key", "content": "good content" }
            ]
            """));
        var sut = CreateSut(handler);

        var facts = await sut.ExtractAsync("u", "a", "conv-1", 1);

        facts.Should().ContainSingle(f => f.Key == "good-key");
    }

    [Fact]
    public async Task ExtractAsync_HttpError_ReturnsEmptyWithoutThrowing()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var sut = CreateSut(handler);

        var facts = await sut.ExtractAsync("u", "a", "conv-1", 1);

        facts.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_EmptyBody_ReturnsEmpty()
    {
        var handler = new StubHandler(_ => Ok("null"));
        var sut = CreateSut(handler);

        var facts = await sut.ExtractAsync("u", "a", "conv-1", 1);

        facts.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_MalformedJson_ReturnsEmptyWithoutThrowing()
    {
        var handler = new StubHandler(_ => Ok("not json"));
        var sut = CreateSut(handler);

        var act = () => sut.ExtractAsync("u", "a", "conv-1", 1);

        var facts = await act.Should().NotThrowAsync();
        facts.Which.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_RemoteCallTimesOut_ReturnsEmptyWithoutPropagating()
    {
        // HttpClient.Timeout surfaces as a TaskCanceledException — an OperationCanceledException —
        // even though the caller's own token was never cancelled; this must still degrade
        // gracefully rather than propagate as if the caller itself cancelled the turn.
        var handler = new StubHandler(_ => throw new TaskCanceledException("timeout", new TimeoutException()));
        var sut = CreateSut(handler);

        var act = () => sut.ExtractAsync("u", "a", "conv-1", 1, CancellationToken.None);

        var facts = await act.Should().NotThrowAsync();
        facts.Which.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_CallerCancels_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = new StubHandler(_ => throw new OperationCanceledException(cts.Token));
        var sut = CreateSut(handler);

        var act = () => sut.ExtractAsync("u", "a", "conv-1", 1, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://avatar.example.com/api/v1/harness-memory/avatar-1/"),
        };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request);
        }
    }
}
