using Application.AI.Common.CQRS.Bundles.RunBundle;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.Bundles;
using Application.Common.Exceptions.ExceptionTypes;
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
    private readonly Mock<IConversationStore> _conversations = new();

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
            _handleStore.Object, _jobStore.Object, _queue.Object, _conversations.Object,
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
        OwnerId = "owner-1",
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
    public async Task Handle_HappyPath_CreatesQueuedRecord_CapturesAgentAndOwner_AndEnqueues()
    {
        _handleStore.Setup(h => h.GetOwner("handle-1")).Returns("owner-1");
        _handleStore.Setup(h => h.TryGet("handle-1")).Returns(Staged());
        BundleRunRecord? created = null;
        _jobStore.Setup(j => j.Create(It.IsAny<BundleRunRecord>())).Callback<BundleRunRecord>(r => created = r);

        var result = await BuildSut(enabled: true).Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        created.Should().NotBeNull();
        created!.Status.Should().Be(BundleRunStatus.Queued);
        created.AgentName.Should().Be("the-agent");
        created.Handle.Should().Be("handle-1");
        created.OwnerId.Should().Be("owner-1");
        created.MaxTurns.Should().Be(4);
        result.Value!.JobId.Should().Be(created.JobId);
        _queue.Verify(q => q.EnqueueAsync(created.JobId, It.IsAny<CancellationToken>()), Times.Once);
    }

    // -- Conversation continuity (#235) --

    [Fact]
    public async Task Handle_WithConversationId_CarriesItOntoTheRunRecord()
    {
        // The id is what makes the run continue a conversation rather than start a throwaway one. If it
        // is dropped here, the run still succeeds — it just silently forgets, which is the failure mode
        // this whole issue is about.
        _handleStore.Setup(h => h.GetOwner("handle-1")).Returns("owner-1");
        _handleStore.Setup(h => h.TryGet("handle-1")).Returns(Staged());
        BundleRunRecord? created = null;
        _jobStore.Setup(j => j.Create(It.IsAny<BundleRunRecord>())).Callback<BundleRunRecord>(r => created = r);

        var result = await BuildSut(enabled: true)
            .Handle(Command() with { ConversationId = "conv-1" }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        created!.ConversationId.Should().Be("conv-1");
    }

    [Fact]
    public async Task Handle_WithoutConversationId_LeavesTheRunSelfContainedAndNeverTouchesTheStore()
    {
        // A one-shot run must not consult the transcript store at all — reaching it would make every
        // bundle run pay for a lookup it has no use for.
        _handleStore.Setup(h => h.GetOwner("handle-1")).Returns("owner-1");
        _handleStore.Setup(h => h.TryGet("handle-1")).Returns(Staged());
        BundleRunRecord? created = null;
        _jobStore.Setup(j => j.Create(It.IsAny<BundleRunRecord>())).Callback<BundleRunRecord>(r => created = r);

        await BuildSut(enabled: true).Handle(Command(), CancellationToken.None);

        created!.ConversationId.Should().BeNull();
        _conversations.Verify(
            c => c.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_ConversationOwnedByAnotherCaller_ReturnsNotFound_AndDoesNotRun()
    {
        // Refused the same way a foreign handle is, and reported identically: a caller must not be able
        // to discover which conversation ids exist for other people by watching the status code change.
        // The refusal has to happen HERE, while the caller is still on the line — deferring it to the
        // background run turns a permission decision into a polled failure.
        _handleStore.Setup(h => h.GetOwner("handle-1")).Returns("owner-1");
        _handleStore.Setup(h => h.TryGet("handle-1")).Returns(Staged());
        _conversations
            .Setup(c => c.GetAsync("conv-theirs", "owner-1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConversationAccessDeniedException());

        var result = await BuildSut(enabled: true)
            .Handle(Command() with { ConversationId = "conv-theirs" }, CancellationToken.None);

        result.FailureType.Should().Be(ResultFailureType.NotFound);
        _jobStore.Verify(j => j.Create(It.IsAny<BundleRunRecord>()), Times.Never);
        _queue.Verify(q => q.EnqueueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_ConversationNotYetCreated_IsAcceptedSoTheRunCanCreateIt()
    {
        // An absent conversation is the ordinary first turn of a new session, not an error. The mocked
        // store returns null by default, which is exactly that case.
        _handleStore.Setup(h => h.GetOwner("handle-1")).Returns("owner-1");
        _handleStore.Setup(h => h.TryGet("handle-1")).Returns(Staged());

        var result = await BuildSut(enabled: true)
            .Handle(Command() with { ConversationId = "conv-new" }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_ChecksTheConversationAgainstTheCallingOwnerNotTheHandleOwner()
    {
        // The store is what refuses a foreign conversation, and it can only do so if this handler hands
        // it the caller. Passing anything else would defeat the check without appearing to remove it.
        _handleStore.Setup(h => h.GetOwner("handle-1")).Returns("owner-1");
        _handleStore.Setup(h => h.TryGet("handle-1")).Returns(Staged());

        await BuildSut(enabled: true)
            .Handle(Command() with { ConversationId = "conv-1" }, CancellationToken.None);

        _conversations.Verify(
            c => c.GetAsync("conv-1", "owner-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_StreamRequested_CreatesStreamingRecord_ButDoesNotEnqueue()
    {
        // A streaming run's sole driver is the caller opening the stream endpoint; enqueuing it too would let
        // the background dispatcher race the stream for the same job.
        _handleStore.Setup(h => h.GetOwner("handle-1")).Returns("owner-1");
        _handleStore.Setup(h => h.TryGet("handle-1")).Returns(Staged());
        BundleRunRecord? created = null;
        _jobStore.Setup(j => j.Create(It.IsAny<BundleRunRecord>())).Callback<BundleRunRecord>(r => created = r);

        var result = await BuildSut(enabled: true)
            .Handle(Command() with { Stream = true }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        created!.Streaming.Should().BeTrue();
        created.Status.Should().Be(BundleRunStatus.Queued);
        _queue.Verify(q => q.EnqueueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_WhenHandleOwnedByAnotherCaller_ReturnsNotFound_AndDoesNotRun()
    {
        // The handle exists, but for a different owner: must be indistinguishable from "not found".
        _handleStore.Setup(h => h.GetOwner("handle-1")).Returns("someone-else");
        _handleStore.Setup(h => h.TryGet("handle-1")).Returns(Staged());

        var result = await BuildSut(enabled: true).Handle(Command(), CancellationToken.None);

        result.FailureType.Should().Be(ResultFailureType.NotFound);
        _jobStore.Verify(j => j.Create(It.IsAny<BundleRunRecord>()), Times.Never);
        _queue.Verify(q => q.EnqueueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
