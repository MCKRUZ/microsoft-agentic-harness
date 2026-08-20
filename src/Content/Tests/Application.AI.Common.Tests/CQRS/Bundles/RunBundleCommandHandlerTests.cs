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

    private static StagedBundle Staged(IReadOnlyList<string>? mcpServerNames = null) => new()
    {
        BundleId = "b1",
        StagedRootDirectory = "/tmp/b1",
        Agent = new AgentDefinition { Id = "the-agent", Name = "The Agent" },
        McpServerNames = mcpServerNames ?? []
    };

    private static RunBundleCommand Command() => new()
    {
        Handle = "handle-1",
        UserMessages = ["hello"],
        Envelope = new CapabilityEnvelope(),
        OwnerId = "owner-1",
        MaxTurns = 4
    };

    private BundleRunRecord? _created;

    /// <summary>Stubs admission to accept, capturing the record the handler built into <see cref="_created"/>.</summary>
    private void AcceptCreate() =>
        _jobStore.Setup(j => j.TryCreate(It.IsAny<BundleRunRecord>(), It.IsAny<int>()))
            .Callback<BundleRunRecord, int>((r, _) => _created = r)
            .Returns(BundleRunAdmission.Accepted);

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
        _jobStore.Verify(j => j.TryCreate(It.IsAny<BundleRunRecord>(), It.IsAny<int>()), Times.Never);
        _queue.Verify(q => q.EnqueueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_HappyPath_CreatesQueuedRecord_CapturesAgentAndOwner_AndEnqueues()
    {
        _handleStore.Setup(h => h.GetOwner("handle-1")).Returns("owner-1");
        _handleStore.Setup(h => h.TryGet("handle-1")).Returns(Staged());
        AcceptCreate();

        var result = await BuildSut(enabled: true).Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _created.Should().NotBeNull();
        _created!.Status.Should().Be(BundleRunStatus.Queued);
        _created.AgentName.Should().Be("the-agent");
        _created.Handle.Should().Be("handle-1");
        _created.OwnerId.Should().Be("owner-1");
        _created.MaxTurns.Should().Be(4);
        result.Value!.JobId.Should().Be(_created.JobId);
        _queue.Verify(q => q.EnqueueAsync(_created.JobId, It.IsAny<CancellationToken>()), Times.Once);
    }

    // -- Bundle-owned MCP server envelope invariant (#376) --

    [Fact]
    public async Task Handle_StagedBundleDeclaresMcpServers_UnionsThemIntoBothAllowedAndBundleOwnedLists()
    {
        // Pins WithBundleOwnedMcpServers — the single production writer CapabilityEnvelope's own remarks
        // name as the one thing actually responsible for keeping AllowedMcpServers and
        // BundleOwnedMcpServers in sync (see CapabilityEnvelopeTests for why no check on the record
        // itself can substitute for this: once a name is missing from BundleOwnedMcpServers, nothing in
        // the envelope retains any evidence it should have been present). Every staged server name must
        // land in BOTH lists from this one call site, or a bundle's own MCP server silently resolves as
        // host-trusted instead of bundle-owned.
        _handleStore.Setup(h => h.GetOwner("handle-1")).Returns("owner-1");
        _handleStore.Setup(h => h.TryGet("handle-1")).Returns(Staged(mcpServerNames: ["b1:echo", "b1:search"]));
        AcceptCreate();

        var result = await BuildSut(enabled: true).Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _created!.Envelope.AllowedMcpServers.Should().Contain(["b1:echo", "b1:search"]);
        _created.Envelope.BundleOwnedMcpServers.Should().Contain(["b1:echo", "b1:search"]);
        _created.Envelope.IsBundleOwnedMcpServer("b1:echo").Should().BeTrue();
        _created.Envelope.IsBundleOwnedMcpServer("b1:search").Should().BeTrue();
    }

    [Fact]
    public async Task Handle_StagedBundleDeclaresNoMcpServers_LeavesBundleOwnedListEmpty()
    {
        // The no-op branch WithBundleOwnedMcpServers takes when staged.McpServerNames is empty must not
        // fabricate entries in either list from the caller's own pre-existing grants.
        _handleStore.Setup(h => h.GetOwner("handle-1")).Returns("owner-1");
        _handleStore.Setup(h => h.TryGet("handle-1")).Returns(Staged());
        AcceptCreate();

        var result = await BuildSut(enabled: true).Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _created!.Envelope.BundleOwnedMcpServers.Should().BeEmpty();
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
        AcceptCreate();

        var result = await BuildSut(enabled: true)
            .Handle(Command() with { ConversationId = "conv-1" }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _created!.ConversationId.Should().Be("conv-1");
    }

    [Fact]
    public async Task Handle_WithoutConversationId_LeavesTheRunSelfContainedAndNeverTouchesTheStore()
    {
        // A one-shot run must not consult the transcript store at all — reaching it would make every
        // bundle run pay for a lookup it has no use for.
        _handleStore.Setup(h => h.GetOwner("handle-1")).Returns("owner-1");
        _handleStore.Setup(h => h.TryGet("handle-1")).Returns(Staged());
        AcceptCreate();

        await BuildSut(enabled: true).Handle(Command(), CancellationToken.None);

        _created!.ConversationId.Should().BeNull();
        _conversations.Verify(
            c => c.GetHistoryForDispatch(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
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
            .Setup(c => c.GetHistoryForDispatch(
                "conv-theirs", "owner-1", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConversationAccessDeniedException());

        var result = await BuildSut(enabled: true)
            .Handle(Command() with { ConversationId = "conv-theirs" }, CancellationToken.None);

        result.FailureType.Should().Be(ResultFailureType.NotFound);
        _jobStore.Verify(j => j.TryCreate(It.IsAny<BundleRunRecord>(), It.IsAny<int>()), Times.Never);
        _queue.Verify(q => q.EnqueueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_ConversationNotYetCreated_IsAcceptedSoTheRunCanCreateIt()
    {
        // An absent conversation is the ordinary first turn of a new session, not an error. The mocked
        // store returns null by default, which is exactly that case.
        _handleStore.Setup(h => h.GetOwner("handle-1")).Returns("owner-1");
        _handleStore.Setup(h => h.TryGet("handle-1")).Returns(Staged());
        AcceptCreate();

        var result = await BuildSut(enabled: true)
            .Handle(Command() with { ConversationId = "conv-new" }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _created!.ConversationId.Should().Be("conv-new");

        // Asserting the probe HAPPENED, not just that the result was a success — success is the default
        // outcome, so without this the test stays green with the whole pre-check deleted.
        _conversations.Verify(
            c => c.GetHistoryForDispatch("conv-new", "owner-1", 0, It.IsAny<CancellationToken>()),
            Times.Once);
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

        // Zero messages: this needs existence and ownership, not the transcript. Asking for the whole
        // conversation to throw it away would put a full transcript load on the synchronous request
        // path of every run — the cost this issue exists to remove, reintroduced one layer down.
        _conversations.Verify(
            c => c.GetHistoryForDispatch("conv-1", "owner-1", 0, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_StreamRequested_CreatesStreamingRecord_ButDoesNotEnqueue()
    {
        // A streaming run's sole driver is the caller opening the stream endpoint; enqueuing it too would let
        // the background dispatcher race the stream for the same job.
        _handleStore.Setup(h => h.GetOwner("handle-1")).Returns("owner-1");
        _handleStore.Setup(h => h.TryGet("handle-1")).Returns(Staged());
        AcceptCreate();

        var result = await BuildSut(enabled: true)
            .Handle(Command() with { Stream = true }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _created!.Streaming.Should().BeTrue();
        _created.Status.Should().Be(BundleRunStatus.Queued);
        _queue.Verify(q => q.EnqueueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // -- Bundle-owned MCP servers (issue #368) --

    [Fact]
    public async Task Handle_StagedBundleWithMcpServerNames_UnionsIntoEnvelopeAllowedMcpServers()
    {
        _handleStore.Setup(h => h.GetOwner("handle-1")).Returns("owner-1");
        _handleStore.Setup(h => h.TryGet("handle-1")).Returns(Staged(mcpServerNames: ["b1:echo"]));
        AcceptCreate();

        var callerEnvelope = new CapabilityEnvelope { AllowedMcpServers = ["host-server"] };
        var result = await BuildSut(enabled: true)
            .Handle(Command() with { Envelope = callerEnvelope }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _created!.Envelope.AllowedMcpServers.Should().BeEquivalentTo(["host-server", "b1:echo"],
            "the bundle's own server must be additively granted, never replacing the caller's own grant");
        _created.Envelope.BundleOwnedMcpServers.Should().BeEquivalentTo(["b1:echo"],
            "the authoritative bundle-ownership record must be stamped at the same point the grant is made, " +
            "not left for a downstream caller to re-derive from the server name's shape");
    }

    [Fact]
    public async Task Handle_StagedBundleWithNoMcpServerNames_LeavesEnvelopeUnchanged()
    {
        _handleStore.Setup(h => h.GetOwner("handle-1")).Returns("owner-1");
        _handleStore.Setup(h => h.TryGet("handle-1")).Returns(Staged());
        AcceptCreate();

        var callerEnvelope = new CapabilityEnvelope { AllowedMcpServers = ["host-server"] };
        var result = await BuildSut(enabled: true)
            .Handle(Command() with { Envelope = callerEnvelope }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _created!.Envelope.Should().BeSameAs(callerEnvelope, "a bundle with no MCP servers must not allocate a new envelope");
    }

    [Fact]
    public async Task Handle_WhenHandleOwnedByAnotherCaller_ReturnsNotFound_AndDoesNotRun()
    {
        // The handle exists, but for a different owner: must be indistinguishable from "not found".
        _handleStore.Setup(h => h.GetOwner("handle-1")).Returns("someone-else");
        _handleStore.Setup(h => h.TryGet("handle-1")).Returns(Staged());

        var result = await BuildSut(enabled: true).Handle(Command(), CancellationToken.None);

        result.FailureType.Should().Be(ResultFailureType.NotFound);
        _jobStore.Verify(j => j.TryCreate(It.IsAny<BundleRunRecord>(), It.IsAny<int>()), Times.Never);
        _queue.Verify(q => q.EnqueueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // -- Admission (#449) --

    [Fact]
    public async Task Handle_ConversationAlreadyRunning_ReturnsConflict_AndDoesNotEnqueue()
    {
        _handleStore.Setup(h => h.GetOwner("handle-1")).Returns("owner-1");
        _handleStore.Setup(h => h.TryGet("handle-1")).Returns(Staged());
        _jobStore.Setup(j => j.TryCreate(It.IsAny<BundleRunRecord>(), It.IsAny<int>()))
            .Returns(BundleRunAdmission.ConversationAlreadyRunning);

        var result = await BuildSut(enabled: true)
            .Handle(Command() with { ConversationId = "conv-1" }, CancellationToken.None);

        result.FailureType.Should().Be(ResultFailureType.Conflict);
        result.Errors.Should().ContainSingle(e => e.Contains("conversation", StringComparison.OrdinalIgnoreCase));
        _queue.Verify(q => q.EnqueueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_OwnerAtCapacity_ReturnsValidationFailure_AndDoesNotEnqueue()
    {
        // Distinct status from ConversationAlreadyRunning (409) on purpose, mirroring
        // StartWorkflowRunCommandHandler.Refuse's split for the identical admission shape: capacity is
        // about the caller's own accepted volume, which the caller fixes by finishing its own work —
        // that is a 400, not a 409 naming a conflict with someone else's run.
        _handleStore.Setup(h => h.GetOwner("handle-1")).Returns("owner-1");
        _handleStore.Setup(h => h.TryGet("handle-1")).Returns(Staged());
        _jobStore.Setup(j => j.TryCreate(It.IsAny<BundleRunRecord>(), It.IsAny<int>()))
            .Returns(BundleRunAdmission.OwnerAtCapacity);

        var result = await BuildSut(enabled: true).Handle(Command(), CancellationToken.None);

        result.FailureType.Should().Be(ResultFailureType.Validation);
        result.Errors.Should().ContainSingle(e => e.Contains("maximum", StringComparison.OrdinalIgnoreCase));
        _queue.Verify(q => q.EnqueueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
