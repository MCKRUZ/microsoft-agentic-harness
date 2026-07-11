using Application.AI.Common.CQRS.Bundles.RunBundle;
using Application.AI.Common.Interfaces.Bundles;
using Domain.AI.Agents;
using Domain.AI.Bundles;
using Domain.Common;
using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.CQRS.Bundles;

/// <summary>
/// Tests for <see cref="RunBundleCommandHandler"/>: the disabled gate, the missing-handle path, and the
/// happy path that creates a queued run record (with the agent name captured from the staged bundle) and
/// enqueues it for dispatch.
/// </summary>
public sealed class RunBundleCommandHandlerTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero));
    private readonly Mock<IBundleHandleStore> _handleStore = new();
    private readonly Mock<IBundleRunJobStore> _jobStore = new();
    private readonly Mock<IBundleRunDispatchQueue> _queue = new();

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private RunBundleCommandHandler BuildSut(bool enabled)
    {
        var cfg = new AppConfig();
        cfg.AI.BundleExecution.Enabled = enabled;
        return new RunBundleCommandHandler(
            _handleStore.Object, _jobStore.Object, _queue.Object,
            new StaticOptionsMonitor<AppConfig>(cfg), _time,
            NullLogger<RunBundleCommandHandler>.Instance);
    }

    private static StagedBundle Staged() => new()
    {
        BundleId = "b1",
        StagedRootDirectory = "/tmp/b1",
        Agent = new AgentDefinition { Id = "the-agent", Name = "The Agent" }
    };

    private static RunBundleCommand Command() => new()
    {
        Handle = "handle-1",
        UserMessages = ["hello"],
        Envelope = new CapabilityEnvelope(),
        MaxTurns = 4
    };

    [Fact]
    public async Task Handle_WhenDisabled_ReturnsForbidden_AndDoesNotEnqueue()
    {
        var result = await BuildSut(enabled: false).Handle(Command(), CancellationToken.None);

        result.FailureType.Should().Be(ResultFailureType.Forbidden);
        _queue.Verify(q => q.EnqueueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenHandleUnknown_ReturnsNotFound_AndDoesNotCreateOrEnqueue()
    {
        _handleStore.Setup(h => h.TryGet("handle-1")).Returns((StagedBundle?)null);

        var result = await BuildSut(enabled: true).Handle(Command(), CancellationToken.None);

        result.FailureType.Should().Be(ResultFailureType.NotFound);
        _jobStore.Verify(j => j.Create(It.IsAny<BundleRunRecord>()), Times.Never);
        _queue.Verify(q => q.EnqueueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_HappyPath_CreatesQueuedRecord_CapturesAgentName_AndEnqueues()
    {
        _handleStore.Setup(h => h.TryGet("handle-1")).Returns(Staged());
        BundleRunRecord? created = null;
        _jobStore.Setup(j => j.Create(It.IsAny<BundleRunRecord>())).Callback<BundleRunRecord>(r => created = r);

        var result = await BuildSut(enabled: true).Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        created.Should().NotBeNull();
        created!.Status.Should().Be(BundleRunStatus.Queued);
        created.AgentName.Should().Be("the-agent");
        created.Handle.Should().Be("handle-1");
        created.MaxTurns.Should().Be(4);
        result.Value!.JobId.Should().Be(created.JobId);
        _queue.Verify(q => q.EnqueueAsync(created.JobId, It.IsAny<CancellationToken>()), Times.Once);
    }
}
