using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Application.AI.Common.Interfaces.Resilience;
using Domain.AI.Resilience;
using FluentAssertions;
using Infrastructure.AI.Resilience;
using Microsoft.Extensions.AI;
using Moq;
using Polly;
using Xunit;

namespace Infrastructure.AI.Tests.Resilience;

/// <summary>
/// Tests for <see cref="ResilientChatClient"/> — the IChatClient wrapper that iterates through
/// a provider fallback chain with per-provider resilience pipelines.
/// </summary>
public sealed class ResilientChatClientTests : IDisposable
{
    private readonly Mock<IProviderHealthMonitor> _healthMonitor = new();
    private ResilientChatClient? _sut;

    public ResilientChatClientTests()
    {
        _healthMonitor.Setup(h => h.GetProviderHealth(It.IsAny<string>()))
            .Returns(ProviderHealthState.Healthy);
        _healthMonitor.Setup(h => h.GetAllProviderHealth())
            .Returns(new Dictionary<string, ProviderHealthState>());
    }

    [Fact]
    public async Task GetResponseAsync_PrimarySucceeds_NoFallback_MetadataShowsPrimary()
    {
        var primary = new FakeChatClient("primary-response");
        var secondary = new FakeChatClient("secondary-response");
        _sut = CreateClient([Entry("primary", primary), Entry("secondary", secondary)]);

        var response = await _sut.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        response.Messages[0].Text.Should().Be("primary-response");
        var metadata = ExtractMetadata(response);
        metadata.IsFallback.Should().BeFalse();
        metadata.ActiveProvider.Should().Be("primary");
        metadata.FailedProviders.Should().BeEmpty();
        secondary.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task GetResponseAsync_PrimaryFails_SecondarySucceeds_MetadataShowsFallback()
    {
        var primary = new FakeChatClient(new HttpRequestException("unavailable"));
        var secondary = new FakeChatClient("fallback-response");
        _sut = CreateClient([Entry("primary", primary), Entry("secondary", secondary)]);

        var response = await _sut.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        response.Messages[0].Text.Should().Be("fallback-response");
        var metadata = ExtractMetadata(response);
        metadata.IsFallback.Should().BeTrue();
        metadata.ActiveProvider.Should().Be("secondary");
        metadata.FailedProviders.Should().ContainSingle().Which.Should().Be("primary");
    }

    [Fact]
    public async Task GetResponseAsync_AllProvidersFail_ThrowsProviderExhaustedException()
    {
        var primary = new FakeChatClient(new HttpRequestException("fail-1"));
        var secondary = new FakeChatClient(new HttpRequestException("fail-2"));
        _sut = CreateClient([Entry("primary", primary), Entry("secondary", secondary)]);

        var act = async () => await _sut.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        var ex = await act.Should().ThrowAsync<ProviderExhaustedException>();
        ex.Which.FailedProviders.Should().BeEquivalentTo(["primary", "secondary"]);
        ex.Which.RetryAfter.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task GetResponseAsync_CircuitOpen_SkipsProvider_TriesNext()
    {
        _healthMonitor.Setup(h => h.GetProviderHealth("primary"))
            .Returns(ProviderHealthState.Unavailable);

        var primary = new FakeChatClient("should-not-be-called");
        var secondary = new FakeChatClient("fallback-response");
        _sut = CreateClient([Entry("primary", primary), Entry("secondary", secondary)]);

        var response = await _sut.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        response.Messages[0].Text.Should().Be("fallback-response");
        primary.CallCount.Should().Be(0);
        var metadata = ExtractMetadata(response);
        metadata.FailedProviders.Should().Contain("primary");
    }

    [Fact]
    public async Task GetResponseAsync_FallbackMetadata_PopulatedCorrectly()
    {
        var first = new FakeChatClient(new HttpRequestException("fail-1"));
        var second = new FakeChatClient(new HttpRequestException("fail-2"));
        var third = new FakeChatClient("success");

        _healthMonitor.Setup(h => h.GetAllProviderHealth())
            .Returns(new Dictionary<string, ProviderHealthState>
            {
                ["first"] = ProviderHealthState.Unavailable,
                ["second"] = ProviderHealthState.Degraded,
                ["third"] = ProviderHealthState.Healthy
            });

        _sut = CreateClient([Entry("first", first), Entry("second", second), Entry("third", third)]);

        var response = await _sut.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        var metadata = ExtractMetadata(response);
        metadata.ActiveProvider.Should().Be("third");
        metadata.FailedProviders.Should().BeEquivalentTo(["first", "second"]);
        metadata.CircuitStates.Should().HaveCount(3);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_PrimaryFails_SecondarySucceeds()
    {
        var primary = new FakeChatClient(new HttpRequestException("stream-fail"));
        var secondary = new FakeStreamingChatClient(["chunk1", "chunk2"]);
        _sut = CreateClient([Entry("primary", primary), Entry("secondary", secondary)]);

        var chunks = new List<string>();
        await foreach (var update in _sut.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
        {
            if (update.Text is not null)
                chunks.Add(update.Text);
        }

        chunks.Should().BeEquivalentTo(["chunk1", "chunk2"]);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_MidStreamFailure_FallsBackToNextProvider()
    {
        var primary = new FakeStreamingChatClient(["partial1", "partial2"], throwAfter: 1);
        var secondary = new FakeStreamingChatClient(["full1", "full2"]);
        _sut = CreateClient([Entry("primary", primary), Entry("secondary", secondary)]);

        var chunks = new List<string>();
        await foreach (var update in _sut.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
        {
            if (update.Text is not null)
                chunks.Add(update.Text);
        }

        // "partial1" was yielded before mid-stream failure, then secondary provides full response
        chunks.Should().BeEquivalentTo(["partial1", "full1", "full2"]);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_AllFail_ThrowsProviderExhaustedException()
    {
        var primary = new FakeChatClient(new HttpRequestException("fail-1"));
        var secondary = new FakeChatClient(new HttpRequestException("fail-2"));
        _sut = CreateClient([Entry("primary", primary), Entry("secondary", secondary)]);

        var act = async () =>
        {
            await foreach (var _ in _sut.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
            { }
        };

        await act.Should().ThrowAsync<ProviderExhaustedException>();
    }

    [Fact]
    public void Dispose_DisposesAllProviderClients()
    {
        var primary = new FakeChatClient("ok");
        var secondary = new FakeChatClient("ok");
        _sut = CreateClient([Entry("primary", primary), Entry("secondary", secondary)]);

        _sut.Dispose();

        primary.IsDisposed.Should().BeTrue();
        secondary.IsDisposed.Should().BeTrue();
    }

    public void Dispose() => _sut?.Dispose();

    private ResilientChatClient CreateClient(IReadOnlyList<ResilientChatClient.ProviderEntry> providers)
    {
        return new ResilientChatClient(providers, _healthMonitor.Object);
    }

    private static ResilientChatClient.ProviderEntry Entry(string name, IChatClient client)
    {
        return new ResilientChatClient.ProviderEntry(name, client, ResiliencePipeline<ChatResponse>.Empty, ResiliencePipeline.Empty);
    }

    private static FallbackMetadata ExtractMetadata(ChatResponse response)
    {
        response.AdditionalProperties.Should().NotBeNull();
        response.AdditionalProperties!.TryGetValue(ResilientChatClient.FallbackMetadataKey, out var raw).Should().BeTrue();
        raw.Should().BeOfType<FallbackMetadata>();
        return (FallbackMetadata)raw!;
    }

    private sealed class FakeChatClient : IChatClient
    {
        private readonly string? _responseText;
        private readonly Exception? _exception;

        public int CallCount { get; private set; }
        public bool IsDisposed { get; private set; }

        public FakeChatClient(string responseText) => _responseText = responseText;
        public FakeChatClient(Exception exception) => _exception = exception;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (_exception is not null)
                throw _exception;
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, _responseText)]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (_exception is not null)
                throw _exception;
            yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent(_responseText)] };
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakeStreamingChatClient : IChatClient
    {
        private readonly string[] _chunks;
        private readonly int? _throwAfter;

        public FakeStreamingChatClient(string[] chunks, int? throwAfter = null)
        {
            _chunks = chunks;
            _throwAfter = throwAfter;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            throw new HttpRequestException("non-streaming not supported");
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var yielded = 0;
            foreach (var chunk in _chunks)
            {
                yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent(chunk)] };
                yielded++;
                if (_throwAfter.HasValue && yielded >= _throwAfter.Value)
                    throw new HttpRequestException("mid-stream failure");
            }
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
