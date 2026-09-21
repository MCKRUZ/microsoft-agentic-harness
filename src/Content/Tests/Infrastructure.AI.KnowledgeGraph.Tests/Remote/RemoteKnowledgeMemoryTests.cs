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
/// write), a failure on <c>RecallAsync</c> degrades to no matches, a caller with no resolved
/// identity is refused before any network call, the local write gate runs before every remote
/// write, and <c>ForgetAsync</c> throws rather than reporting a false success.
/// </summary>
public sealed class RemoteKnowledgeMemoryTests
{
    private static RemoteKnowledgeMemory CreateSut(
        StubHandler handler,
        string? conversationId = "conv-1",
        string? userId = "user-1",
        string? tenantId = null,
        IMemoryWriteGate? writeGate = null)
    {
        var scope = new Mock<IKnowledgeScope>();
        scope.SetupGet(s => s.ConversationId).Returns(conversationId);
        scope.SetupGet(s => s.UserId).Returns(userId);
        scope.SetupGet(s => s.TenantId).Returns(tenantId);
        return new RemoteKnowledgeMemory(
            new StubHttpClientFactory(handler),
            scope.Object,
            writeGate ?? AllowGate(),
            NullLogger<RemoteKnowledgeMemory>.Instance);
    }

    private static IMemoryWriteGate AllowGate()
    {
        var gate = new Mock<IMemoryWriteGate>();
        gate.Setup(g => g.EvaluateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MemoryWriteDecision.Allow());
        return gate.Object;
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
    public async Task RememberAsync_PostsToRememberWithConversationIdAsThreadIdAndCallerIdentity()
    {
        var handler = new StubHandler(_ => Ok("""{ "persist": false, "trust": 1, "reason": "quarantined" }"""));
        var sut = CreateSut(handler, conversationId: "conv-99", userId: "alice", tenantId: "acme");

        await sut.RememberAsync("key", "content");

        handler.LastRequestUri!.AbsolutePath.Should().EndWith("/remember");
        handler.LastRequestBody.Should().Contain("\"threadId\":\"conv-99\"");
        handler.LastRequestBody.Should().Contain("\"userId\":\"alice\"");
        handler.LastRequestBody.Should().Contain("\"tenantId\":\"acme\"");
    }

    [Fact]
    public async Task RememberAsync_NullConversationId_FallsBackToUnscopedThreadId()
    {
        var handler = new StubHandler(_ => Ok("""{ "persist": true, "trust": 0, "reason": "trusted" }"""));
        var sut = CreateSut(handler, conversationId: null);

        await sut.RememberAsync("key", "content");

        handler.LastRequestBody.Should().Contain("\"threadId\":\"unscoped\"");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RememberAsync_NoResolvedUserId_RefusesWithoutCallingTheNetwork(string? userId)
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("should never be called"));
        var sut = CreateSut(handler, userId: userId);

        var decision = await sut.RememberAsync("key", "content");

        decision.Persist.Should().BeFalse("a caller with no resolved identity must never write into a shared unscoped bucket");
    }

    [Fact]
    public async Task RememberAsync_LocalGateRejects_NeverCallsTheNetwork()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("should never be called"));
        var gate = new Mock<IMemoryWriteGate>();
        gate.Setup(g => g.EvaluateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryWriteDecision { Persist = false, Trust = MemoryTrust.Untrusted, Reason = "rejected: injection detected" });
        var sut = CreateSut(handler, writeGate: gate.Object);

        var decision = await sut.RememberAsync("key", "malicious content");

        decision.Persist.Should().BeFalse();
        decision.Reason.Should().Be("rejected: injection detected");
    }

    [Fact]
    public async Task RememberAsync_LocalGateQuarantines_NeverCallsTheNetwork()
    {
        // The remote wire contract carries no trust field, so a quarantined fact sent to /remember
        // would come back on a later /recall indistinguishable from a trusted one. Quarantine must
        // stop here, the same as Reject — never send it to a system with no concept of "hidden."
        var handler = new StubHandler(_ => throw new InvalidOperationException("should never be called"));
        var gate = new Mock<IMemoryWriteGate>();
        gate.Setup(g => g.EvaluateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryWriteDecision { Persist = true, Trust = MemoryTrust.Untrusted, Reason = "quarantined locally" });
        var sut = CreateSut(handler, writeGate: gate.Object);

        var decision = await sut.RememberAsync("key", "content");

        decision.Trust.Should().Be(MemoryTrust.Untrusted);
        decision.Reason.Should().Be("quarantined locally");
    }

    [Fact]
    public async Task RememberAsync_RemoteDowngradesLocalTrustedResponse_StaysUntrusted()
    {
        // A fully-Trusted local decision is the only thing ever sent; the remote gate can still
        // narrow it further, never widen it.
        var handler = new StubHandler(_ => Ok("""{ "persist": true, "trust": 1, "reason": "remote quarantine" }"""));
        var sut = CreateSut(handler);

        var decision = await sut.RememberAsync("key", "content");

        decision.Trust.Should().Be(MemoryTrust.Untrusted);
    }

    [Fact]
    public async Task RememberAsync_RemoteCallTimesOut_ReturnsRejectedWithoutPropagating()
    {
        // HttpClient.Timeout surfaces as a TaskCanceledException — an OperationCanceledException —
        // even though the caller's own token was never cancelled. This must still degrade
        // gracefully, not propagate as if the caller itself cancelled the operation.
        var handler = new StubHandler(_ => throw new TaskCanceledException("timeout", new TimeoutException()));
        var sut = CreateSut(handler);

        var act = () => sut.RememberAsync("key", "content", cancellationToken: CancellationToken.None);

        var decision = await act.Should().NotThrowAsync();
        decision.Which.Persist.Should().BeFalse();
    }

    [Fact]
    public async Task RememberAsync_CallerCancels_PropagatesCancellation()
    {
        // The opposite of the timeout case: when the caller's own token requested cancellation,
        // that must still propagate rather than being swallowed into a rejected decision.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = new StubHandler(_ => throw new OperationCanceledException(cts.Token));
        var sut = CreateSut(handler);

        var act = () => sut.RememberAsync("key", "content", cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
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
    public async Task RecallAsync_SuccessResponse_MapsToGraphNodesWithFixedHonestType()
    {
        var handler = new StubHandler(_ => Ok("""
            [ { "id": "mem-1", "content": "User likes blue", "score": 0.87 } ]
            """));
        var sut = CreateSut(handler);

        var nodes = await sut.RecallAsync("favorite color", entityType: "Person");

        nodes.Should().ContainSingle();
        nodes[0].Id.Should().Be("mem-1");
        nodes[0].Properties["content"].Should().Be("User likes blue");
        nodes[0].Type.Should().Be("Fact", "the remote contract carries no per-result type — the caller's filter must never be fabricated onto the result");
    }

    [Fact]
    public async Task RecallAsync_PostsCallerIdentity()
    {
        var handler = new StubHandler(_ => Ok("[]"));
        var sut = CreateSut(handler, userId: "bob", tenantId: "acme");

        await sut.RecallAsync("query");

        handler.LastRequestBody.Should().Contain("\"userId\":\"bob\"");
        handler.LastRequestBody.Should().Contain("\"tenantId\":\"acme\"");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task RecallAsync_NoResolvedUserId_RefusesWithoutCallingTheNetwork(string? userId)
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("should never be called"));
        var sut = CreateSut(handler, userId: userId);

        var nodes = await sut.RecallAsync("query");

        nodes.Should().BeEmpty();
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
    public async Task RecallAsync_RemoteCallTimesOut_ReturnsEmptyWithoutPropagating()
    {
        var handler = new StubHandler(_ => throw new TaskCanceledException("timeout", new TimeoutException()));
        var sut = CreateSut(handler);

        var act = () => sut.RecallAsync("query", cancellationToken: CancellationToken.None);

        var nodes = await act.Should().NotThrowAsync();
        nodes.Which.Should().BeEmpty();
    }

    [Fact]
    public async Task RecallAsync_CallerCancels_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = new StubHandler(_ => throw new OperationCanceledException(cts.Token));
        var sut = CreateSut(handler);

        var act = () => sut.RecallAsync("query", cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ForgetAsync_ThrowsInsteadOfClaimingSuccess()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("should never be called"));
        var sut = CreateSut(handler);

        var act = () => sut.ForgetAsync("key");

        await act.Should().ThrowAsync<NotImplementedException>(
            "a fake 204 for a delete that never happened is a right-to-erasure defect, not a harmless no-op");
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
