namespace Infrastructure.AI.Tests.Resilience;

using Application.AI.Common.Interfaces.Resilience;
using Domain.AI.Resilience;
using Domain.Common.Config.AI.Resilience;
using FluentAssertions;
using Infrastructure.AI.Resilience;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

/// <summary>
/// Tests for <see cref="LlmRetryQueue"/> — the in-memory retry queue with TTL enforcement
/// and circuit-recovery-triggered drain. Tests run against the public/internal methods
/// directly (EnqueueAsync, DrainAsync, SweepExpired) without starting the
/// BackgroundService lifecycle.
/// </summary>
public sealed class LlmRetryQueueTests : IDisposable
{
    private readonly Mock<IProviderHealthMonitor> _healthMonitor = new();
    private readonly Mock<IResilientChatClientProvider> _clientProvider = new();
    private readonly Mock<IChatClient> _chatClient = new();
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly ResilienceConfig _config;
    private readonly LlmRetryQueue _sut;

    public LlmRetryQueueTests()
    {
        _config = new ResilienceConfig
        {
            Enabled = true,
            DegradedMode = new DegradedModeConfig
            {
                MaxQueueSize = 5,
                RetryQueueTtlSeconds = 10
            }
        };

        var optionsMonitor = new Mock<IOptionsMonitor<ResilienceConfig>>();
        optionsMonitor.Setup(o => o.CurrentValue).Returns(_config);

        _clientProvider
            .Setup(p => p.GetResilientChatClientAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(_chatClient.Object);

        _healthMonitor.Setup(h => h.IsAnyProviderHealthy()).Returns(true);

        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 5, 10, 12, 0, 0, TimeSpan.Zero));

        _sut = new LlmRetryQueue(
            _healthMonitor.Object,
            _clientProvider.Object,
            optionsMonitor.Object,
            _timeProvider,
            NullLogger<LlmRetryQueue>.Instance);
    }

    public void Dispose() => _sut.Dispose();

    private static IList<ChatMessage> TestMessages() =>
        [new ChatMessage(ChatRole.User, "test prompt")];

    private static ChatResponse TestResponse() =>
        new(new ChatMessage(ChatRole.Assistant, "test response"));

    [Fact]
    public void EnqueueAsync_AddsToQueue_ReturnsIncompleteTask()
    {
        var task = _sut.EnqueueAsync(TestMessages(), null, CancellationToken.None);

        task.IsCompleted.Should().BeFalse();
        _sut.QueueDepth.Should().Be(1);
    }

    [Fact]
    public void EnqueueAsync_ExceedsMaxSize_RejectsOldest()
    {
        _config.DegradedMode.MaxQueueSize = 3;

        var tasks = new Task<ChatResponse>[4];
        for (var i = 0; i < 4; i++)
            tasks[i] = _sut.EnqueueAsync(TestMessages(), null, CancellationToken.None);

        _sut.QueueDepth.Should().Be(3);

        tasks[0].IsFaulted.Should().BeTrue();
        tasks[0].Exception!.InnerException.Should().BeOfType<ProviderExhaustedException>();

        tasks[3].IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task DrainAsync_ProviderRecovered_RetriesQueuedRequests()
    {
        _chatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestResponse());

        var task1 = _sut.EnqueueAsync(TestMessages(), null, CancellationToken.None);
        var task2 = _sut.EnqueueAsync(TestMessages(), null, CancellationToken.None);

        await _sut.DrainAsync(CancellationToken.None);

        task1.IsCompletedSuccessfully.Should().BeTrue();
        task2.IsCompletedSuccessfully.Should().BeTrue();
        _sut.QueueDepth.Should().Be(0);

        _chatClient.Verify(
            c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task DrainAsync_CallerCancelled_SkipsRequest()
    {
        _chatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestResponse());

        using var cts = new CancellationTokenSource();
        var cancelledTask = _sut.EnqueueAsync(TestMessages(), null, cts.Token);
        var normalTask = _sut.EnqueueAsync(TestMessages(), null, CancellationToken.None);

        cts.Cancel();

        await _sut.DrainAsync(CancellationToken.None);

        cancelledTask.IsCanceled.Should().BeTrue();
        normalTask.IsCompletedSuccessfully.Should().BeTrue();

        _chatClient.Verify(
            c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void SweepExpired_TtlExpired_CompletesWithProviderExhaustedException()
    {
        var task = _sut.EnqueueAsync(TestMessages(), null, CancellationToken.None);

        _timeProvider.Advance(TimeSpan.FromSeconds(11));

        _sut.SweepExpired();

        task.IsFaulted.Should().BeTrue();
        task.Exception!.InnerException.Should().BeOfType<ProviderExhaustedException>();
        _sut.QueueDepth.Should().Be(0);
    }

    [Fact]
    public async Task DrainAsync_SuccessfulRetry_CompletesTcs()
    {
        var expectedResponse = TestResponse();
        _chatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);

        var task = _sut.EnqueueAsync(TestMessages(), null, CancellationToken.None);

        await _sut.DrainAsync(CancellationToken.None);

        task.IsCompletedSuccessfully.Should().BeTrue();
        var result = await task;
        result.Should().BeSameAs(expectedResponse);
        _sut.QueueDepth.Should().Be(0);
    }

    [Fact]
    public async Task DrainAsync_RetryFails_RequeuesItem()
    {
        _chatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ProviderExhaustedException(["test-provider"], TimeSpan.FromSeconds(30)));

        var task = _sut.EnqueueAsync(TestMessages(), null, CancellationToken.None);

        await _sut.DrainAsync(CancellationToken.None);

        task.IsCompleted.Should().BeFalse();
        _sut.QueueDepth.Should().Be(1);
    }

    [Fact]
    public async Task DrainAsync_NoHealthyProvider_DoesNotAttemptRetry()
    {
        _healthMonitor.Setup(h => h.IsAnyProviderHealthy()).Returns(false);

        _ = _sut.EnqueueAsync(TestMessages(), null, CancellationToken.None);

        await _sut.DrainAsync(CancellationToken.None);

        _sut.QueueDepth.Should().Be(1);
        _chatClient.Verify(
            c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
