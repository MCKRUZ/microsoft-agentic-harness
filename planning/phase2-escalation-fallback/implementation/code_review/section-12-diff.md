diff --git a/src/Content/Infrastructure/Infrastructure.AI/Resilience/ResilientChatClient.cs b/src/Content/Infrastructure/Infrastructure.AI/Resilience/ResilientChatClient.cs
new file mode 100644
index 0000000..f146f46
--- /dev/null
+++ b/src/Content/Infrastructure/Infrastructure.AI/Resilience/ResilientChatClient.cs
@@ -0,0 +1,227 @@
+using System.Collections.Immutable;
+using System.Runtime.CompilerServices;
+using Application.AI.Common.Interfaces.Resilience;
+using Application.AI.Common.OpenTelemetry.Metrics;
+using Domain.AI.Resilience;
+using Domain.AI.Telemetry.Conventions;
+using Microsoft.Extensions.AI;
+using Microsoft.Extensions.Logging;
+using Polly;
+
+namespace Infrastructure.AI.Resilience;
+
+/// <summary>
+/// An <see cref="IChatClient"/> wrapper that iterates through an ordered provider fallback chain,
+/// executing each provider through its own per-provider Polly resilience pipeline. Transparently
+/// handles retries, circuit breaker tripping, provider failover, and <see cref="FallbackMetadata"/>
+/// attachment to responses.
+/// </summary>
+/// <remarks>
+/// <para>
+/// The cross-provider fallback is a simple iteration loop, NOT a Polly fallback strategy.
+/// Each provider's pipeline handles per-provider retry, circuit breaking, and timeout.
+/// When a provider's pipeline throws (meaning all retries are exhausted or circuit is open),
+/// the loop advances to the next provider.
+/// </para>
+/// <para>
+/// Thread-safe for concurrent calls — all mutable state is per-call (stack-local).
+/// </para>
+/// </remarks>
+public sealed class ResilientChatClient : IChatClient
+{
+    /// <summary>Well-known key for <see cref="FallbackMetadata"/> in response additional properties.</summary>
+    public const string FallbackMetadataKey = "FallbackMetadata";
+
+    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(60);
+
+    private readonly IReadOnlyList<ProviderEntry> _providers;
+    private readonly IProviderHealthMonitor _healthMonitor;
+    private readonly ILogger<ResilientChatClient>? _logger;
+
+    /// <summary>
+    /// Creates a resilient chat client wrapping an ordered provider fallback chain.
+    /// </summary>
+    /// <param name="providers">Ordered provider entries. First is primary, rest are fallbacks.</param>
+    /// <param name="healthMonitor">Provides circuit breaker health state for skip-on-open logic.</param>
+    /// <param name="logger">Optional logger.</param>
+    /// <exception cref="ArgumentException">Thrown when <paramref name="providers"/> is empty.</exception>
+    public ResilientChatClient(
+        IReadOnlyList<ProviderEntry> providers,
+        IProviderHealthMonitor healthMonitor,
+        ILogger<ResilientChatClient>? logger = null)
+    {
+        if (providers.Count == 0)
+            throw new ArgumentException("At least one provider is required.", nameof(providers));
+
+        _providers = providers;
+        _healthMonitor = healthMonitor;
+        _logger = logger;
+    }
+
+    /// <inheritdoc/>
+    public async Task<ChatResponse> GetResponseAsync(
+        IEnumerable<ChatMessage> messages,
+        ChatOptions? options = null,
+        CancellationToken cancellationToken = default)
+    {
+        var failedProviders = new List<string>();
+        Exception? lastException = null;
+
+        foreach (var provider in _providers)
+        {
+            if (_healthMonitor.GetProviderHealth(provider.Name) == ProviderHealthState.Unavailable)
+            {
+                _logger?.LogDebug("Skipping provider {Provider} — circuit open", provider.Name);
+                failedProviders.Add(provider.Name);
+                continue;
+            }
+
+            try
+            {
+                var response = await provider.Pipeline.ExecuteAsync(
+                    async ct => await provider.Client.GetResponseAsync(messages, options, ct),
+                    cancellationToken);
+
+                AttachMetadata(response, provider.Name, failedProviders);
+
+                if (failedProviders.Count > 0)
+                {
+                    ResilienceMetrics.FallbackActivations.Add(1,
+                        new KeyValuePair<string, object?>(ResilienceConventions.ProviderName, provider.Name));
+                }
+
+                return response;
+            }
+            catch (Exception ex)
+            {
+                lastException = ex;
+                _logger?.LogWarning(ex, "Provider {Provider} failed, attempting next", provider.Name);
+                failedProviders.Add(provider.Name);
+            }
+        }
+
+        ResilienceMetrics.DegradationEvents.Add(1);
+        throw new ProviderExhaustedException(failedProviders, DefaultRetryAfter, lastException!);
+    }
+
+    /// <inheritdoc/>
+    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
+        IEnumerable<ChatMessage> messages,
+        ChatOptions? options = null,
+        [EnumeratorCancellation] CancellationToken cancellationToken = default)
+    {
+        var failedProviders = new List<string>();
+
+        foreach (var provider in _providers)
+        {
+            if (_healthMonitor.GetProviderHealth(provider.Name) == ProviderHealthState.Unavailable)
+            {
+                _logger?.LogDebug("Skipping streaming provider {Provider} — circuit open", provider.Name);
+                failedProviders.Add(provider.Name);
+                continue;
+            }
+
+            var succeeded = false;
+
+            IAsyncEnumerable<ChatResponseUpdate>? stream = null;
+            try
+            {
+                stream = provider.Client.GetStreamingResponseAsync(messages, options, cancellationToken);
+            }
+            catch (Exception ex)
+            {
+                _logger?.LogWarning(ex, "Stream initiation failed for provider {Provider}", provider.Name);
+                failedProviders.Add(provider.Name);
+                continue;
+            }
+
+            var enumerator = stream.GetAsyncEnumerator(cancellationToken);
+            try
+            {
+                while (true)
+                {
+                    bool hasNext;
+                    try
+                    {
+                        hasNext = await enumerator.MoveNextAsync();
+                    }
+                    catch (Exception ex)
+                    {
+                        _logger?.LogWarning(ex, "Mid-stream failure for provider {Provider}", provider.Name);
+                        failedProviders.Add(provider.Name);
+                        break;
+                    }
+
+                    if (!hasNext)
+                    {
+                        succeeded = true;
+                        break;
+                    }
+
+                    yield return enumerator.Current;
+                }
+            }
+            finally
+            {
+                await enumerator.DisposeAsync();
+            }
+
+            if (succeeded)
+            {
+                if (failedProviders.Count > 0)
+                {
+                    ResilienceMetrics.FallbackActivations.Add(1,
+                        new KeyValuePair<string, object?>(ResilienceConventions.ProviderName, provider.Name));
+                }
+                yield break;
+            }
+        }
+
+        ResilienceMetrics.DegradationEvents.Add(1);
+        throw new ProviderExhaustedException(failedProviders, DefaultRetryAfter);
+    }
+
+    /// <inheritdoc/>
+    public object? GetService(Type serviceType, object? serviceKey = null)
+    {
+        if (serviceType == typeof(ChatClientMetadata))
+            return new ChatClientMetadata(nameof(ResilientChatClient));
+        return null;
+    }
+
+    /// <inheritdoc/>
+    public void Dispose()
+    {
+        foreach (var provider in _providers)
+        {
+            provider.Client.Dispose();
+        }
+    }
+
+    private void AttachMetadata(ChatResponse response, string activeProvider, List<string> failedProviders)
+    {
+        var metadata = new FallbackMetadata
+        {
+            ActiveProvider = activeProvider,
+            IsFallback = failedProviders.Count > 0,
+            FailedProviders = failedProviders.ToArray(),
+            DisabledCapabilities = ImmutableHashSet<string>.Empty,
+            CircuitStates = _healthMonitor.GetAllProviderHealth()
+        };
+
+        response.AdditionalProperties ??= new AdditionalPropertiesDictionary();
+        response.AdditionalProperties[FallbackMetadataKey] = metadata;
+    }
+
+    /// <summary>
+    /// Represents a single provider entry in the fallback chain with its associated
+    /// resilience pipeline.
+    /// </summary>
+    /// <param name="Name">Logical provider identifier (e.g., "azure-openai", "anthropic").</param>
+    /// <param name="Client">The underlying chat client for this provider.</param>
+    /// <param name="Pipeline">The per-provider Polly resilience pipeline wrapping calls to <paramref name="Client"/>.</param>
+    public sealed record ProviderEntry(
+        string Name,
+        IChatClient Client,
+        ResiliencePipeline<ChatResponse> Pipeline);
+}
diff --git a/src/Content/Tests/Infrastructure.AI.Tests/Resilience/ResilientChatClientTests.cs b/src/Content/Tests/Infrastructure.AI.Tests/Resilience/ResilientChatClientTests.cs
new file mode 100644
index 0000000..0901dbe
--- /dev/null
+++ b/src/Content/Tests/Infrastructure.AI.Tests/Resilience/ResilientChatClientTests.cs
@@ -0,0 +1,275 @@
+using System.Collections.Immutable;
+using System.Runtime.CompilerServices;
+using Application.AI.Common.Interfaces.Resilience;
+using Domain.AI.Resilience;
+using FluentAssertions;
+using Infrastructure.AI.Resilience;
+using Microsoft.Extensions.AI;
+using Moq;
+using Polly;
+using Xunit;
+
+namespace Infrastructure.AI.Tests.Resilience;
+
+/// <summary>
+/// Tests for <see cref="ResilientChatClient"/> — the IChatClient wrapper that iterates through
+/// a provider fallback chain with per-provider resilience pipelines.
+/// </summary>
+public sealed class ResilientChatClientTests : IDisposable
+{
+    private readonly Mock<IProviderHealthMonitor> _healthMonitor = new();
+    private ResilientChatClient? _sut;
+
+    public ResilientChatClientTests()
+    {
+        _healthMonitor.Setup(h => h.GetProviderHealth(It.IsAny<string>()))
+            .Returns(ProviderHealthState.Healthy);
+        _healthMonitor.Setup(h => h.GetAllProviderHealth())
+            .Returns(new Dictionary<string, ProviderHealthState>());
+    }
+
+    [Fact]
+    public async Task GetResponseAsync_PrimarySucceeds_NoFallback_MetadataShowsPrimary()
+    {
+        var primary = new FakeChatClient("primary-response");
+        var secondary = new FakeChatClient("secondary-response");
+        _sut = CreateClient([Entry("primary", primary), Entry("secondary", secondary)]);
+
+        var response = await _sut.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);
+
+        response.Messages[0].Text.Should().Be("primary-response");
+        var metadata = ExtractMetadata(response);
+        metadata.IsFallback.Should().BeFalse();
+        metadata.ActiveProvider.Should().Be("primary");
+        metadata.FailedProviders.Should().BeEmpty();
+        secondary.CallCount.Should().Be(0);
+    }
+
+    [Fact]
+    public async Task GetResponseAsync_PrimaryFails_SecondarySucceeds_MetadataShowsFallback()
+    {
+        var primary = new FakeChatClient(new HttpRequestException("unavailable"));
+        var secondary = new FakeChatClient("fallback-response");
+        _sut = CreateClient([Entry("primary", primary), Entry("secondary", secondary)]);
+
+        var response = await _sut.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);
+
+        response.Messages[0].Text.Should().Be("fallback-response");
+        var metadata = ExtractMetadata(response);
+        metadata.IsFallback.Should().BeTrue();
+        metadata.ActiveProvider.Should().Be("secondary");
+        metadata.FailedProviders.Should().ContainSingle().Which.Should().Be("primary");
+    }
+
+    [Fact]
+    public async Task GetResponseAsync_AllProvidersFail_ThrowsProviderExhaustedException()
+    {
+        var primary = new FakeChatClient(new HttpRequestException("fail-1"));
+        var secondary = new FakeChatClient(new HttpRequestException("fail-2"));
+        _sut = CreateClient([Entry("primary", primary), Entry("secondary", secondary)]);
+
+        var act = async () => await _sut.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);
+
+        var ex = await act.Should().ThrowAsync<ProviderExhaustedException>();
+        ex.Which.FailedProviders.Should().BeEquivalentTo(["primary", "secondary"]);
+        ex.Which.RetryAfter.Should().BeGreaterThan(TimeSpan.Zero);
+    }
+
+    [Fact]
+    public async Task GetResponseAsync_CircuitOpen_SkipsProvider_TriesNext()
+    {
+        _healthMonitor.Setup(h => h.GetProviderHealth("primary"))
+            .Returns(ProviderHealthState.Unavailable);
+
+        var primary = new FakeChatClient("should-not-be-called");
+        var secondary = new FakeChatClient("fallback-response");
+        _sut = CreateClient([Entry("primary", primary), Entry("secondary", secondary)]);
+
+        var response = await _sut.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);
+
+        response.Messages[0].Text.Should().Be("fallback-response");
+        primary.CallCount.Should().Be(0);
+        var metadata = ExtractMetadata(response);
+        metadata.FailedProviders.Should().Contain("primary");
+    }
+
+    [Fact]
+    public async Task GetResponseAsync_FallbackMetadata_PopulatedCorrectly()
+    {
+        var first = new FakeChatClient(new HttpRequestException("fail-1"));
+        var second = new FakeChatClient(new HttpRequestException("fail-2"));
+        var third = new FakeChatClient("success");
+
+        _healthMonitor.Setup(h => h.GetAllProviderHealth())
+            .Returns(new Dictionary<string, ProviderHealthState>
+            {
+                ["first"] = ProviderHealthState.Unavailable,
+                ["second"] = ProviderHealthState.Degraded,
+                ["third"] = ProviderHealthState.Healthy
+            });
+
+        _sut = CreateClient([Entry("first", first), Entry("second", second), Entry("third", third)]);
+
+        var response = await _sut.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);
+
+        var metadata = ExtractMetadata(response);
+        metadata.ActiveProvider.Should().Be("third");
+        metadata.FailedProviders.Should().BeEquivalentTo(["first", "second"]);
+        metadata.CircuitStates.Should().HaveCount(3);
+    }
+
+    [Fact]
+    public async Task GetStreamingResponseAsync_PrimaryFails_SecondarySucceeds()
+    {
+        var primary = new FakeChatClient(new HttpRequestException("stream-fail"));
+        var secondary = new FakeStreamingChatClient(["chunk1", "chunk2"]);
+        _sut = CreateClient([Entry("primary", primary), Entry("secondary", secondary)]);
+
+        var chunks = new List<string>();
+        await foreach (var update in _sut.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
+        {
+            if (update.Text is not null)
+                chunks.Add(update.Text);
+        }
+
+        chunks.Should().BeEquivalentTo(["chunk1", "chunk2"]);
+    }
+
+    [Fact]
+    public async Task GetStreamingResponseAsync_MidStreamFailure_FallsBackToNextProvider()
+    {
+        var primary = new FakeStreamingChatClient(["partial1", "partial2"], throwAfter: 1);
+        var secondary = new FakeStreamingChatClient(["full1", "full2"]);
+        _sut = CreateClient([Entry("primary", primary), Entry("secondary", secondary)]);
+
+        var chunks = new List<string>();
+        await foreach (var update in _sut.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
+        {
+            if (update.Text is not null)
+                chunks.Add(update.Text);
+        }
+
+        // "partial1" was yielded before mid-stream failure, then secondary provides full response
+        chunks.Should().BeEquivalentTo(["partial1", "full1", "full2"]);
+    }
+
+    [Fact]
+    public async Task GetStreamingResponseAsync_AllFail_ThrowsProviderExhaustedException()
+    {
+        var primary = new FakeChatClient(new HttpRequestException("fail-1"));
+        var secondary = new FakeChatClient(new HttpRequestException("fail-2"));
+        _sut = CreateClient([Entry("primary", primary), Entry("secondary", secondary)]);
+
+        var act = async () =>
+        {
+            await foreach (var _ in _sut.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
+            { }
+        };
+
+        await act.Should().ThrowAsync<ProviderExhaustedException>();
+    }
+
+    [Fact]
+    public void Dispose_DisposesAllProviderClients()
+    {
+        var primary = new FakeChatClient("ok");
+        var secondary = new FakeChatClient("ok");
+        _sut = CreateClient([Entry("primary", primary), Entry("secondary", secondary)]);
+
+        _sut.Dispose();
+
+        primary.IsDisposed.Should().BeTrue();
+        secondary.IsDisposed.Should().BeTrue();
+    }
+
+    public void Dispose() => _sut?.Dispose();
+
+    private ResilientChatClient CreateClient(IReadOnlyList<ResilientChatClient.ProviderEntry> providers)
+    {
+        return new ResilientChatClient(providers, _healthMonitor.Object);
+    }
+
+    private static ResilientChatClient.ProviderEntry Entry(string name, IChatClient client)
+    {
+        return new ResilientChatClient.ProviderEntry(name, client, ResiliencePipeline<ChatResponse>.Empty);
+    }
+
+    private static FallbackMetadata ExtractMetadata(ChatResponse response)
+    {
+        response.AdditionalProperties.Should().NotBeNull();
+        response.AdditionalProperties!.TryGetValue(ResilientChatClient.FallbackMetadataKey, out var raw).Should().BeTrue();
+        raw.Should().BeOfType<FallbackMetadata>();
+        return (FallbackMetadata)raw!;
+    }
+
+    private sealed class FakeChatClient : IChatClient
+    {
+        private readonly string? _responseText;
+        private readonly Exception? _exception;
+
+        public int CallCount { get; private set; }
+        public bool IsDisposed { get; private set; }
+
+        public FakeChatClient(string responseText) => _responseText = responseText;
+        public FakeChatClient(Exception exception) => _exception = exception;
+
+        public Task<ChatResponse> GetResponseAsync(
+            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
+        {
+            CallCount++;
+            if (_exception is not null)
+                throw _exception;
+            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, _responseText)]));
+        }
+
+        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
+            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
+            [EnumeratorCancellation] CancellationToken cancellationToken = default)
+        {
+            CallCount++;
+            if (_exception is not null)
+                throw _exception;
+            yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent(_responseText)] };
+            await Task.CompletedTask;
+        }
+
+        public object? GetService(Type serviceType, object? serviceKey = null) => null;
+        public void Dispose() => IsDisposed = true;
+    }
+
+    private sealed class FakeStreamingChatClient : IChatClient
+    {
+        private readonly string[] _chunks;
+        private readonly int? _throwAfter;
+
+        public FakeStreamingChatClient(string[] chunks, int? throwAfter = null)
+        {
+            _chunks = chunks;
+            _throwAfter = throwAfter;
+        }
+
+        public Task<ChatResponse> GetResponseAsync(
+            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
+        {
+            throw new HttpRequestException("non-streaming not supported");
+        }
+
+        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
+            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
+            [EnumeratorCancellation] CancellationToken cancellationToken = default)
+        {
+            var yielded = 0;
+            foreach (var chunk in _chunks)
+            {
+                yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent(chunk)] };
+                yielded++;
+                if (_throwAfter.HasValue && yielded >= _throwAfter.Value)
+                    throw new HttpRequestException("mid-stream failure");
+            }
+            await Task.CompletedTask;
+        }
+
+        public object? GetService(Type serviceType, object? serviceKey = null) => null;
+        public void Dispose() { }
+    }
+}
