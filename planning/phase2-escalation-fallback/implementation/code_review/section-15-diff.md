diff --git a/src/Content/Infrastructure/Infrastructure.AI/Infrastructure.AI.csproj b/src/Content/Infrastructure/Infrastructure.AI/Infrastructure.AI.csproj
index 6930cc7..df24613 100644
--- a/src/Content/Infrastructure/Infrastructure.AI/Infrastructure.AI.csproj
+++ b/src/Content/Infrastructure/Infrastructure.AI/Infrastructure.AI.csproj
@@ -21,6 +21,10 @@
     <PackageReference Include="Polly.Core" />
   </ItemGroup>
 
+  <ItemGroup>
+    <InternalsVisibleTo Include="Infrastructure.AI.Tests" />
+  </ItemGroup>
+
   <ItemGroup>
     <ProjectReference Include="..\..\Application\Application.AI.Common\Application.AI.Common.csproj" />
     <ProjectReference Include="..\..\Application\Application.Core\Application.Core.csproj" />
diff --git a/src/Content/Infrastructure/Infrastructure.AI/Resilience/LlmRetryQueue.cs b/src/Content/Infrastructure/Infrastructure.AI/Resilience/LlmRetryQueue.cs
new file mode 100644
index 0000000..555a324
--- /dev/null
+++ b/src/Content/Infrastructure/Infrastructure.AI/Resilience/LlmRetryQueue.cs
@@ -0,0 +1,290 @@
+using System.Collections.Concurrent;
+using Application.AI.Common.Interfaces.Resilience;
+using Application.AI.Common.OpenTelemetry.Metrics;
+using Domain.AI.Resilience;
+using Domain.Common.Config.AI.Resilience;
+using Microsoft.Extensions.AI;
+using Microsoft.Extensions.Hosting;
+using Microsoft.Extensions.Logging;
+using Microsoft.Extensions.Options;
+
+namespace Infrastructure.AI.Resilience;
+
+/// <summary>
+/// Represents a queued LLM request awaiting retry after all providers were exhausted.
+/// </summary>
+internal sealed record QueuedLlmRequest
+{
+    /// <summary>The original chat messages to retry.</summary>
+    public required IList<ChatMessage> Messages { get; init; }
+
+    /// <summary>The original chat options (model, temperature, etc.).</summary>
+    public ChatOptions? Options { get; init; }
+
+    /// <summary>
+    /// Completion source that callers await. Completed with the response on successful
+    /// retry, or with <see cref="ProviderExhaustedException"/> on TTL expiry.
+    /// </summary>
+    public required TaskCompletionSource<ChatResponse> CompletionSource { get; init; }
+
+    /// <summary>When this request was enqueued. Used for TTL enforcement.</summary>
+    public required DateTimeOffset EnqueuedAt { get; init; }
+
+    /// <summary>Absolute expiry time (EnqueuedAt + TTL).</summary>
+    public required DateTimeOffset ExpiresAt { get; init; }
+
+    /// <summary>
+    /// The original caller's cancellation token. Checked before retry to avoid
+    /// wasting LLM tokens on abandoned requests.
+    /// </summary>
+    public required CancellationToken CallerCancellation { get; init; }
+}
+
+/// <summary>
+/// In-memory retry queue for LLM requests that failed due to all providers being
+/// exhausted. Monitors circuit breaker recovery via <see cref="IProviderHealthMonitor"/>
+/// and automatically retries queued requests when a provider becomes healthy.
+/// </summary>
+/// <remarks>
+/// <para>
+/// Conditionally registered as <see cref="IHostedService"/> only when
+/// <c>ResilienceConfig.Enabled == true</c>. When resilience is disabled, this
+/// service is not in the DI container at all.
+/// </para>
+/// <para>
+/// The queue is bounded by <see cref="DegradedModeConfig.MaxQueueSize"/>. When full,
+/// the oldest request is evicted and its <see cref="TaskCompletionSource{T}"/> is
+/// completed with <see cref="ProviderExhaustedException"/>.
+/// </para>
+/// <para>
+/// TTL enforcement runs on a periodic sweep (every 10 seconds). Expired items have
+/// their TCS completed with <see cref="ProviderExhaustedException"/>.
+/// </para>
+/// </remarks>
+public sealed class LlmRetryQueue : BackgroundService
+{
+    private readonly IProviderHealthMonitor _healthMonitor;
+    private readonly IResilientChatClientProvider _chatClientProvider;
+    private readonly IOptionsMonitor<ResilienceConfig> _resilienceConfig;
+    private readonly TimeProvider _timeProvider;
+    private readonly ILogger<LlmRetryQueue> _logger;
+    private readonly ConcurrentQueue<QueuedLlmRequest> _queue = new();
+    private readonly SemaphoreSlim _drainSignal = new(0, 1);
+    private int _queueDepth;
+    private IChatClient? _cachedClient;
+
+    /// <summary>Creates a new retry queue instance.</summary>
+    /// <param name="healthMonitor">Health monitor for circuit breaker state queries and recovery events.</param>
+    /// <param name="chatClientProvider">Provider for the resilient chat client used to retry requests.</param>
+    /// <param name="resilienceConfig">Configuration for queue size and TTL.</param>
+    /// <param name="timeProvider">Time provider for TTL calculations (testable via FakeTimeProvider).</param>
+    /// <param name="logger">Logger for queue operations.</param>
+    public LlmRetryQueue(
+        IProviderHealthMonitor healthMonitor,
+        IResilientChatClientProvider chatClientProvider,
+        IOptionsMonitor<ResilienceConfig> resilienceConfig,
+        TimeProvider timeProvider,
+        ILogger<LlmRetryQueue> logger)
+    {
+        _healthMonitor = healthMonitor;
+        _chatClientProvider = chatClientProvider;
+        _resilienceConfig = resilienceConfig;
+        _timeProvider = timeProvider;
+        _logger = logger;
+    }
+
+    /// <summary>Current queue depth. Tracked via Interlocked for O(1) reads.</summary>
+    internal int QueueDepth => _queueDepth;
+
+    /// <summary>
+    /// Enqueues a failed LLM request for automatic retry when a provider recovers.
+    /// Returns a Task that completes when the request is eventually retried successfully
+    /// or expires.
+    /// </summary>
+    /// <param name="messages">The original chat messages.</param>
+    /// <param name="options">The original chat options.</param>
+    /// <param name="callerCancellation">The original caller's cancellation token.</param>
+    /// <returns>A Task that completes with the ChatResponse on successful retry.</returns>
+    public Task<ChatResponse> EnqueueAsync(
+        IList<ChatMessage> messages,
+        ChatOptions? options,
+        CancellationToken callerCancellation)
+    {
+        var config = _resilienceConfig.CurrentValue.DegradedMode;
+        var now = _timeProvider.GetUtcNow();
+        var tcs = new TaskCompletionSource<ChatResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
+
+        var request = new QueuedLlmRequest
+        {
+            Messages = messages,
+            Options = options,
+            CompletionSource = tcs,
+            EnqueuedAt = now,
+            ExpiresAt = now.AddSeconds(config.RetryQueueTtlSeconds),
+            CallerCancellation = callerCancellation
+        };
+
+        _queue.Enqueue(request);
+        var depth = Interlocked.Increment(ref _queueDepth);
+        ResilienceMetrics.QueueSize.Add(1);
+
+        while (depth > config.MaxQueueSize && _queue.TryDequeue(out var evicted))
+        {
+            Interlocked.Decrement(ref _queueDepth);
+            ResilienceMetrics.QueueSize.Add(-1);
+            ResilienceMetrics.QueueExpired.Add(1);
+            evicted.CompletionSource.TrySetException(
+                new ProviderExhaustedException(Array.Empty<string>(), TimeSpan.Zero));
+            depth = _queueDepth;
+        }
+
+        return tcs.Task;
+    }
+
+    /// <summary>
+    /// Removes TTL-expired items from the queue, completing their TCS with
+    /// <see cref="ProviderExhaustedException"/>. Non-expired items are re-enqueued.
+    /// </summary>
+    internal void SweepExpired()
+    {
+        var now = _timeProvider.GetUtcNow();
+        var count = _queueDepth;
+
+        for (var i = 0; i < count; i++)
+        {
+            if (!_queue.TryDequeue(out var item))
+                break;
+
+            if (item.ExpiresAt <= now)
+            {
+                Interlocked.Decrement(ref _queueDepth);
+                ResilienceMetrics.QueueSize.Add(-1);
+                ResilienceMetrics.QueueExpired.Add(1);
+                item.CompletionSource.TrySetException(
+                    new ProviderExhaustedException(Array.Empty<string>(), TimeSpan.Zero));
+            }
+            else
+            {
+                _queue.Enqueue(item);
+            }
+        }
+    }
+
+    /// <summary>
+    /// Attempts to retry all queued requests using the resilient chat client.
+    /// Skips cancelled requests, re-enqueues on provider exhaustion, and removes
+    /// successfully completed or failed items.
+    /// </summary>
+    /// <param name="cancellationToken">Cancellation token for the drain operation.</param>
+    internal async Task DrainAsync(CancellationToken cancellationToken)
+    {
+        if (!_healthMonitor.IsAnyProviderHealthy())
+            return;
+
+        _cachedClient ??= await _chatClientProvider.GetResilientChatClientAsync(cancellationToken);
+
+        var count = _queueDepth;
+        for (var i = 0; i < count; i++)
+        {
+            if (!_queue.TryDequeue(out var item))
+                break;
+
+            if (item.CallerCancellation.IsCancellationRequested)
+            {
+                Interlocked.Decrement(ref _queueDepth);
+                ResilienceMetrics.QueueSize.Add(-1);
+                item.CompletionSource.TrySetCanceled(item.CallerCancellation);
+                continue;
+            }
+
+            if (item.ExpiresAt <= _timeProvider.GetUtcNow())
+            {
+                Interlocked.Decrement(ref _queueDepth);
+                ResilienceMetrics.QueueSize.Add(-1);
+                ResilienceMetrics.QueueExpired.Add(1);
+                item.CompletionSource.TrySetException(
+                    new ProviderExhaustedException(Array.Empty<string>(), TimeSpan.Zero));
+                continue;
+            }
+
+            try
+            {
+                var response = await _cachedClient.GetResponseAsync(
+                    item.Messages, item.Options, item.CallerCancellation);
+
+                Interlocked.Decrement(ref _queueDepth);
+                ResilienceMetrics.QueueSize.Add(-1);
+                item.CompletionSource.TrySetResult(response);
+                _logger.LogDebug("Retry queue drained request successfully");
+            }
+            catch (ProviderExhaustedException)
+            {
+                _queue.Enqueue(item);
+                _logger.LogWarning("Retry failed during drain — providers exhausted again, re-enqueued");
+                break;
+            }
+            catch (Exception ex)
+            {
+                Interlocked.Decrement(ref _queueDepth);
+                ResilienceMetrics.QueueSize.Add(-1);
+                item.CompletionSource.TrySetException(ex);
+                _logger.LogWarning(ex, "Retry queue request failed with unexpected exception");
+            }
+        }
+    }
+
+    /// <inheritdoc/>
+    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
+    {
+        void OnStateChanged(string providerName, ProviderHealthState newState)
+        {
+            if (newState == ProviderHealthState.Healthy)
+            {
+                try { _drainSignal.Release(); }
+                catch (SemaphoreFullException) { /* coalesce — already signaled */ }
+            }
+        }
+
+        _healthMonitor.OnCircuitStateChanged += OnStateChanged;
+
+        try
+        {
+            while (!stoppingToken.IsCancellationRequested)
+            {
+                await _drainSignal.WaitAsync(TimeSpan.FromSeconds(10), stoppingToken)
+                    .ConfigureAwait(false);
+
+                SweepExpired();
+
+                if (_healthMonitor.IsAnyProviderHealthy())
+                    await DrainAsync(stoppingToken).ConfigureAwait(false);
+            }
+        }
+        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
+        {
+            // Expected during shutdown
+        }
+        finally
+        {
+            _healthMonitor.OnCircuitStateChanged -= OnStateChanged;
+
+            var abandoned = 0;
+            while (_queue.TryDequeue(out var item))
+            {
+                Interlocked.Decrement(ref _queueDepth);
+                item.CompletionSource.TrySetCanceled(CancellationToken.None);
+                abandoned++;
+            }
+
+            if (abandoned > 0)
+                _logger.LogWarning("LlmRetryQueue shutting down, abandoned {Count} queued requests", abandoned);
+        }
+    }
+
+    /// <inheritdoc/>
+    public override void Dispose()
+    {
+        _drainSignal.Dispose();
+        base.Dispose();
+    }
+}
diff --git a/src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj b/src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj
index 70aae10..52b5326 100644
--- a/src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj
+++ b/src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj
@@ -17,6 +17,7 @@
     <PackageReference Include="Microsoft.Extensions.Configuration.UserSecrets" />
     <PackageReference Include="FluentAssertions" />
     <PackageReference Include="Microsoft.NET.Test.Sdk" />
+    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />
     <PackageReference Include="Moq" />
     <PackageReference Include="xunit" />
     <PackageReference Include="xunit.runner.visualstudio" />
diff --git a/src/Content/Tests/Infrastructure.AI.Tests/Resilience/LlmRetryQueueTests.cs b/src/Content/Tests/Infrastructure.AI.Tests/Resilience/LlmRetryQueueTests.cs
new file mode 100644
index 0000000..6983993
--- /dev/null
+++ b/src/Content/Tests/Infrastructure.AI.Tests/Resilience/LlmRetryQueueTests.cs
@@ -0,0 +1,221 @@
+namespace Infrastructure.AI.Tests.Resilience;
+
+using Application.AI.Common.Interfaces.Resilience;
+using Domain.AI.Resilience;
+using Domain.Common.Config.AI.Resilience;
+using FluentAssertions;
+using Infrastructure.AI.Resilience;
+using Microsoft.Extensions.AI;
+using Microsoft.Extensions.Logging.Abstractions;
+using Microsoft.Extensions.Options;
+using Microsoft.Extensions.Time.Testing;
+using Moq;
+using Xunit;
+
+/// <summary>
+/// Tests for <see cref="LlmRetryQueue"/> — the in-memory retry queue with TTL enforcement
+/// and circuit-recovery-triggered drain. Tests run against the public/internal methods
+/// directly (EnqueueAsync, DrainAsync, SweepExpired) without starting the
+/// BackgroundService lifecycle.
+/// </summary>
+public sealed class LlmRetryQueueTests : IDisposable
+{
+    private readonly Mock<IProviderHealthMonitor> _healthMonitor = new();
+    private readonly Mock<IResilientChatClientProvider> _clientProvider = new();
+    private readonly Mock<IChatClient> _chatClient = new();
+    private readonly FakeTimeProvider _timeProvider = new();
+    private readonly ResilienceConfig _config;
+    private readonly LlmRetryQueue _sut;
+
+    public LlmRetryQueueTests()
+    {
+        _config = new ResilienceConfig
+        {
+            Enabled = true,
+            DegradedMode = new DegradedModeConfig
+            {
+                MaxQueueSize = 5,
+                RetryQueueTtlSeconds = 10
+            }
+        };
+
+        var optionsMonitor = new Mock<IOptionsMonitor<ResilienceConfig>>();
+        optionsMonitor.Setup(o => o.CurrentValue).Returns(_config);
+
+        _clientProvider
+            .Setup(p => p.GetResilientChatClientAsync(It.IsAny<CancellationToken>()))
+            .ReturnsAsync(_chatClient.Object);
+
+        _healthMonitor.Setup(h => h.IsAnyProviderHealthy()).Returns(true);
+
+        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 5, 10, 12, 0, 0, TimeSpan.Zero));
+
+        _sut = new LlmRetryQueue(
+            _healthMonitor.Object,
+            _clientProvider.Object,
+            optionsMonitor.Object,
+            _timeProvider,
+            NullLogger<LlmRetryQueue>.Instance);
+    }
+
+    public void Dispose() => _sut.Dispose();
+
+    private static IList<ChatMessage> TestMessages() =>
+        [new ChatMessage(ChatRole.User, "test prompt")];
+
+    private static ChatResponse TestResponse() =>
+        new(new ChatMessage(ChatRole.Assistant, "test response"));
+
+    [Fact]
+    public void EnqueueAsync_AddsToQueue_ReturnsIncompleteTask()
+    {
+        var task = _sut.EnqueueAsync(TestMessages(), null, CancellationToken.None);
+
+        task.IsCompleted.Should().BeFalse();
+        _sut.QueueDepth.Should().Be(1);
+    }
+
+    [Fact]
+    public void EnqueueAsync_ExceedsMaxSize_RejectsOldest()
+    {
+        _config.DegradedMode.MaxQueueSize = 3;
+
+        var tasks = new Task<ChatResponse>[4];
+        for (var i = 0; i < 4; i++)
+            tasks[i] = _sut.EnqueueAsync(TestMessages(), null, CancellationToken.None);
+
+        _sut.QueueDepth.Should().Be(3);
+
+        tasks[0].IsFaulted.Should().BeTrue();
+        tasks[0].Exception!.InnerException.Should().BeOfType<ProviderExhaustedException>();
+
+        tasks[3].IsCompleted.Should().BeFalse();
+    }
+
+    [Fact]
+    public async Task DrainAsync_ProviderRecovered_RetriesQueuedRequests()
+    {
+        _chatClient
+            .Setup(c => c.GetResponseAsync(
+                It.IsAny<IList<ChatMessage>>(),
+                It.IsAny<ChatOptions?>(),
+                It.IsAny<CancellationToken>()))
+            .ReturnsAsync(TestResponse());
+
+        var task1 = _sut.EnqueueAsync(TestMessages(), null, CancellationToken.None);
+        var task2 = _sut.EnqueueAsync(TestMessages(), null, CancellationToken.None);
+
+        await _sut.DrainAsync(CancellationToken.None);
+
+        task1.IsCompletedSuccessfully.Should().BeTrue();
+        task2.IsCompletedSuccessfully.Should().BeTrue();
+        _sut.QueueDepth.Should().Be(0);
+
+        _chatClient.Verify(
+            c => c.GetResponseAsync(
+                It.IsAny<IList<ChatMessage>>(),
+                It.IsAny<ChatOptions?>(),
+                It.IsAny<CancellationToken>()),
+            Times.Exactly(2));
+    }
+
+    [Fact]
+    public async Task DrainAsync_CallerCancelled_SkipsRequest()
+    {
+        _chatClient
+            .Setup(c => c.GetResponseAsync(
+                It.IsAny<IList<ChatMessage>>(),
+                It.IsAny<ChatOptions?>(),
+                It.IsAny<CancellationToken>()))
+            .ReturnsAsync(TestResponse());
+
+        using var cts = new CancellationTokenSource();
+        var cancelledTask = _sut.EnqueueAsync(TestMessages(), null, cts.Token);
+        var normalTask = _sut.EnqueueAsync(TestMessages(), null, CancellationToken.None);
+
+        cts.Cancel();
+
+        await _sut.DrainAsync(CancellationToken.None);
+
+        cancelledTask.IsCanceled.Should().BeTrue();
+        normalTask.IsCompletedSuccessfully.Should().BeTrue();
+
+        _chatClient.Verify(
+            c => c.GetResponseAsync(
+                It.IsAny<IList<ChatMessage>>(),
+                It.IsAny<ChatOptions?>(),
+                It.IsAny<CancellationToken>()),
+            Times.Once);
+    }
+
+    [Fact]
+    public void SweepExpired_TtlExpired_CompletesWithProviderExhaustedException()
+    {
+        var task = _sut.EnqueueAsync(TestMessages(), null, CancellationToken.None);
+
+        _timeProvider.Advance(TimeSpan.FromSeconds(11));
+
+        _sut.SweepExpired();
+
+        task.IsFaulted.Should().BeTrue();
+        task.Exception!.InnerException.Should().BeOfType<ProviderExhaustedException>();
+        _sut.QueueDepth.Should().Be(0);
+    }
+
+    [Fact]
+    public async Task DrainAsync_SuccessfulRetry_CompletesTcs()
+    {
+        var expectedResponse = TestResponse();
+        _chatClient
+            .Setup(c => c.GetResponseAsync(
+                It.IsAny<IList<ChatMessage>>(),
+                It.IsAny<ChatOptions?>(),
+                It.IsAny<CancellationToken>()))
+            .ReturnsAsync(expectedResponse);
+
+        var task = _sut.EnqueueAsync(TestMessages(), null, CancellationToken.None);
+
+        await _sut.DrainAsync(CancellationToken.None);
+
+        task.IsCompletedSuccessfully.Should().BeTrue();
+        var result = await task;
+        result.Should().BeSameAs(expectedResponse);
+        _sut.QueueDepth.Should().Be(0);
+    }
+
+    [Fact]
+    public async Task DrainAsync_RetryFails_RequeuesItem()
+    {
+        _chatClient
+            .Setup(c => c.GetResponseAsync(
+                It.IsAny<IList<ChatMessage>>(),
+                It.IsAny<ChatOptions?>(),
+                It.IsAny<CancellationToken>()))
+            .ThrowsAsync(new ProviderExhaustedException(["test-provider"], TimeSpan.FromSeconds(30)));
+
+        var task = _sut.EnqueueAsync(TestMessages(), null, CancellationToken.None);
+
+        await _sut.DrainAsync(CancellationToken.None);
+
+        task.IsCompleted.Should().BeFalse();
+        _sut.QueueDepth.Should().Be(1);
+    }
+
+    [Fact]
+    public async Task DrainAsync_NoHealthyProvider_DoesNotAttemptRetry()
+    {
+        _healthMonitor.Setup(h => h.IsAnyProviderHealthy()).Returns(false);
+
+        _ = _sut.EnqueueAsync(TestMessages(), null, CancellationToken.None);
+
+        await _sut.DrainAsync(CancellationToken.None);
+
+        _sut.QueueDepth.Should().Be(1);
+        _chatClient.Verify(
+            c => c.GetResponseAsync(
+                It.IsAny<IList<ChatMessage>>(),
+                It.IsAny<ChatOptions?>(),
+                It.IsAny<CancellationToken>()),
+            Times.Never);
+    }
+}
diff --git a/src/Directory.Packages.props b/src/Directory.Packages.props
index 92de0e9..5e498e9 100644
--- a/src/Directory.Packages.props
+++ b/src/Directory.Packages.props
@@ -105,6 +105,7 @@
     <PackageVersion Include="Serilog.Formatting.Compact" Version="3.0.0" />
 
     <!-- Testing -->
+    <PackageVersion Include="Microsoft.Extensions.TimeProvider.Testing" Version="10.0.5" />
     <PackageVersion Include="Microsoft.AspNetCore.SignalR.Client" Version="10.0.5" />
     <PackageVersion Include="Microsoft.Playwright" Version="1.51.0" />
     <PackageVersion Include="coverlet.collector" Version="6.0.4" />
