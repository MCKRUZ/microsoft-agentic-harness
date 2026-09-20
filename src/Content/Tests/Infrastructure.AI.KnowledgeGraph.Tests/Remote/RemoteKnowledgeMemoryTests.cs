using System.Net;
using System.Text;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.Remote;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Remote;

/// <summary>
/// Tests for <see cref="RemoteKnowledgeMemory"/> against a stubbed avatar
/// <c>/remember</c>/<c>/recall</c> endpoint pair. Pins the fail-safe contract: an HTTP failure or
/// malformed body on <c>RememberAsync</c> is treated as a rejection (never a persisted, trusted
/// write), a failure on <c>RecallAsync</c> degrades to no matches, and <c>ForgetAsync</c>/
/// <c>ImproveAsync</c> are true no-ops that never call the network at all.
/// </summary>
public sealed class RemoteKnowledgeMemoryTests
{
    private static RemoteKnowledgeMemory CreateSut(StubHandler handler, string? conversationId = "conv-1")
    {
        var scope = new Mock<IKnowledgeScope>();
        scope.SetupGet(s => s.ConversationId).Returns(conversationId);
        return new RemoteKnowledgeMemory(
            new StubHttpClientFactory(handler), scope.Object, NullLogger<RemoteKnowledgeMemory>.Instance);
    }

    [Fact]
    public async Task RememberAsync_SuccessResponse_MapsToMemoryWriteDecision()
    {
        var handler = new StubHandler(_ => Ok("""{ "persist": true, "trust": 0, "reason": "trusted" }"""));
        var sut = CreateSut(handler);

        var decision = await sut.RememberAsync("key", "content");

        decision.Persist.Should().BeTrue();
        decision.Trust.Should().Be(MemoryTrust.Trusted);
        decision.Reason.Should().Be("trusted");
    }

    [Fact]
    public async Task RememberAsync_PostsToRememberWithConversationIdAsThreadId()
    {
        var handler = new StubHandler(_ => Ok("""{ "persist": false, "trust": 1, "reason": "quarantined" }"""));
        var sut = CreateSut(handler, conversationId: "conv-99");

        await sut.RememberAsync("key", "content");

        handler.LastRequestUri!.AbsolutePath.Should().EndWith("/remember");
        handler.LastRequestBody.Should().Contain("\"threadId\":\"conv-99\"");
    }

    [Fact]
    public async Task RememberAsync_NullConversationId_FallsBackToUnscoped()
    {
        var handler = new StubHandler(_ => Ok("""{ "persist": true, "trust": 0, "reason": "trusted" }"""));
        var sut = CreateSut(handler, conversationId: null);

        await sut.RememberAsync("key", "content");

        handler.LastRequestBody.Should().Contain("\"threadId\":\"unscoped\"");
    }

    [Fact]
    public async Task RememberAsync_HttpError_ReturnsRejectedWithoutThrowing()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var sut = CreateSut(handler);

        var decision = await sut.RememberAsync("key", "content");

        decision.Persist.Should().BeFalse();
        decision.Trust.Should().Be(MemoryTrust.Untrusted);
    }

    [Fact]
    public async Task RememberAsync_ThrowingHandler_ReturnsRejectedWithoutPropagating()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        var sut = CreateSut(handler);

        var act = () => sut.RememberAsync("key", "content");

        var decision = await act.Should().NotThrowAsync();
        decision.Which.Persist.Should().BeFalse();
    }

    [Fact]
    public async Task RecallAsync_SuccessResponse_MapsToGraphNodes()
    {
        var handler = new StubHandler(_ => Ok("""
            [ { "id": "mem-1", "content": "User likes blue", "score": 0.87 } ]
            """));
        var sut = CreateSut(handler);

        var nodes = await sut.RecallAsync("favorite color");

        nodes.Should().ContainSingle();
        nodes[0].Id.Should().Be("mem-1");
        nodes[0].Properties["content"].Should().Be("User likes blue");
    }

    [Fact]
    public async Task RecallAsync_HttpError_ReturnsEmptyWithoutThrowing()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var sut = CreateSut(handler);

        var nodes = await sut.RecallAsync("query");

        nodes.Should().BeEmpty();
    }

    [Fact]
    public async Task ForgetAsync_NeverCallsTheNetwork()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("should never be called"));
        var sut = CreateSut(handler);

        var act = () => sut.ForgetAsync("key");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ImproveAsync_NeverCallsTheNetwork()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("should never be called"));
        var sut = CreateSut(handler);

        var act = () => sut.ImproveAsync("user msg", "assistant msg", ["node-1"]);

        await act.Should().NotThrowAsync();
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
