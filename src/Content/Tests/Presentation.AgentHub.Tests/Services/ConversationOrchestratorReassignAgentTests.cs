using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Services.AI;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Domain.AI.Budget;
using FluentAssertions;
using Infrastructure.AI.Conversations;
using MediatR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Presentation.AgentHub.Config;
using Presentation.AgentHub.Interfaces;
using Presentation.AgentHub.Services;
using Xunit;
using Application.AI.Common.Models.Conversations;

namespace Presentation.AgentHub.Tests.Services;

/// <summary>
/// Unit tests for <see cref="ConversationOrchestrator.ReassignAgentAsync"/> — split out from
/// <see cref="ConversationOrchestratorTests"/> (already well past this project's file-size
/// convention before this feature existed) rather than making that file larger, mirroring the
/// existing <c>AgentsControllerReassignAgentTests</c> split for the same feature's controller side.
/// </summary>
public sealed class ConversationOrchestratorReassignAgentTests
{
    private readonly Mock<IMediator> _mediator = new();
    private readonly Mock<IConversationStore> _store = new();
    private readonly Mock<IAgentConversationCache> _agentCache = new();
    private readonly Mock<ISessionHealthTracker> _healthTracker = new();
    private readonly Mock<IObservabilityStore> _obsStore = new();
    private readonly Mock<IConnectionTracker> _connectionTracker = new();
    private readonly Mock<IConversationBudgetTracker> _budget = new();
    private readonly Mock<IToolCallReplayTreatment> _toolCallReplayTreatment = new();
    // The real in-process lease, not a mock: a mocked lease would hand back a null handle, and every
    // test here is specifically about the real serialization it provides. See
    // ConversationOrchestratorTests' own field for the same reasoning.
    private readonly IConversationTurnLease _turnLease = new InProcessConversationTurnLease();
    private readonly AgentHubConfig _config = new() { MaxHistoryMessages = 20 };

    public ConversationOrchestratorReassignAgentTests()
    {
        _budget
            .Setup(b => b.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConversationBudgetStatus.Disabled);
        _toolCallReplayTreatment.Setup(t => t.Enabled).Returns(true);
        _toolCallReplayTreatment.Setup(t => t.MaxCallsPerTurn).Returns(32);
        _toolCallReplayTreatment.Setup(t => t.MaxReplayedChars).Returns(65536);
    }

    private ConversationOrchestrator CreateOrchestrator()
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Development");
        return new(
            _mediator.Object,
            _store.Object,
            _turnLease,
            _agentCache.Object,
            _healthTracker.Object,
            _obsStore.Object,
            new ConversationTelemetryRecorder(
                _obsStore.Object, _store.Object, NullLogger<ConversationTelemetryRecorder>.Instance),
            _connectionTracker.Object,
            _budget.Object,
            _toolCallReplayTreatment.Object,
            Options.Create(_config),
            environment.Object,
            NullLogger<ConversationOrchestrator>.Instance);
    }

    [Fact]
    public async Task ReassignAgentAsync_ConversationNotFound_ReturnsNullAndDoesNotWriteOrEvict()
    {
        _store.Setup(s => s.GetAsync("c1", "user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConversationRecord?)null);

        var orchestrator = CreateOrchestrator();
        var updated = await orchestrator.ReassignAgentAsync("c1", "user1", "new-agent", CancellationToken.None);

        updated.Should().BeNull();
        _store.Verify(s => s.ReassignAgentAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _agentCache.Verify(c => c.Evict(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ReassignAgentAsync_RequestedNameMatchesCurrent_StillWritesButSkipsEviction()
    {
        // A no-op reassignment (client resending the same agentName, e.g. a "confirm current
        // agent" control) must not discard a live, correctly-configured cached agent for no
        // behavioral reason -- see the remarks on IConversationOrchestrator.ReassignAgentAsync.
        // The WRITE still happens, though: every version of this call before this fix went
        // straight to the store with no equality check at all, and that write's UpdatedAt bump is
        // what keeps a conversation correctly sorted in the conversation list. Skipping the write
        // entirely on a same-name request would silently change that ordering behavior.
        var record = new ConversationRecord("c1", "same-agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        _store.Setup(s => s.GetAsync("c1", "user1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.ReassignAgentAsync("c1", "user1", "same-agent", It.IsAny<CancellationToken>()))
            .ReturnsAsync(record);

        var orchestrator = CreateOrchestrator();
        var updated = await orchestrator.ReassignAgentAsync("c1", "user1", "same-agent", CancellationToken.None);

        updated.Should().Be(record);
        _store.Verify(s => s.ReassignAgentAsync("c1", "user1", "same-agent", It.IsAny<CancellationToken>()), Times.Once);
        _agentCache.Verify(c => c.Evict(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ReassignAgentAsync_RequestedNameMatchesCurrentByCaseOnly_StillWritesButSkipsEviction()
    {
        // Same-name comparison is case-insensitive, matching the convention other agent-identifier
        // comparisons in this codebase use (e.g. CapabilityMatchSupervisor's target/calling-agent
        // check) -- a differently-cased resend of the same logical agent must not be treated as a
        // real reassignment for eviction purposes either, though the write (and its UpdatedAt bump)
        // still happens exactly as it would for any other reassignment call.
        var record = new ConversationRecord("c1", "dashboard-agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        var updatedRecord = record with { AgentName = "Dashboard-Agent" };
        _store.Setup(s => s.GetAsync("c1", "user1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.ReassignAgentAsync("c1", "user1", "Dashboard-Agent", It.IsAny<CancellationToken>()))
            .ReturnsAsync(updatedRecord);

        var orchestrator = CreateOrchestrator();
        var updated = await orchestrator.ReassignAgentAsync("c1", "user1", "Dashboard-Agent", CancellationToken.None);

        updated.Should().Be(updatedRecord);
        _store.Verify(s => s.ReassignAgentAsync(
            "c1", "user1", "Dashboard-Agent", It.IsAny<CancellationToken>()), Times.Once);
        _agentCache.Verify(c => c.Evict(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ReassignAgentAsync_Success_WritesThenEvictsTheCachedAgent()
    {
        var record = new ConversationRecord("c1", "old-agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        var updatedRecord = record with { AgentName = "new-agent" };
        _store.Setup(s => s.GetAsync("c1", "user1", It.IsAny<CancellationToken>())).ReturnsAsync(record);
        _store.Setup(s => s.ReassignAgentAsync("c1", "user1", "new-agent", It.IsAny<CancellationToken>()))
            .ReturnsAsync(updatedRecord);

        var orchestrator = CreateOrchestrator();
        var updated = await orchestrator.ReassignAgentAsync("c1", "user1", "new-agent", CancellationToken.None);

        updated.Should().Be(updatedRecord);
        _agentCache.Verify(c => c.Evict("c1"), Times.Once);
    }

    /// <summary>
    /// Proves the fix for the race a code review caught in an earlier version of this feature: the
    /// database write and the cache eviction happen as two separate steps, so without a shared lock
    /// a turn racing the reassignment could read the newly-written agent name yet still be served
    /// the stale cached agent (<see cref="IAgentConversationCache.GetOrCreateAsync"/> returns a
    /// cache hit unconditionally, and eviction is not guaranteed to have run yet). Both operations
    /// now happen while holding the same per-conversation turn lease
    /// <see cref="IConversationOrchestrator.SendMessageAsync"/> acquires before dispatch, so a
    /// concurrent turn cannot get between the write and the eviction at all -- it blocks on the
    /// lease itself until the reassignment fully completes.
    /// </summary>
    [Fact]
    public async Task ReassignAgentAsync_WhileInFlight_BlocksAConcurrentSendMessageFromDispatching()
    {
        var record = new ConversationRecord("c1", "old-agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        _store.Setup(s => s.GetAsync("c1", "user1", It.IsAny<CancellationToken>())).ReturnsAsync(record);

        var reassignmentEntered = new TaskCompletionSource();
        var releaseReassignment = new TaskCompletionSource();
        _store.Setup(s => s.ReassignAgentAsync("c1", "user1", "new-agent", It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                // Signals the test that this call is now running INSIDE the acquired lease, then
                // holds the lease open until the test says to release it -- simulating the window
                // between the store write and the cache eviction that used to be unsynchronized.
                reassignmentEntered.SetResult();
                await releaseReassignment.Task;
                return record with { AgentName = "new-agent" };
            });

        var orchestrator = CreateOrchestrator();

        var reassignTask = orchestrator.ReassignAgentAsync("c1", "user1", "new-agent", CancellationToken.None);
        await reassignmentEntered.Task;

        // A concurrent ordinary turn for the SAME conversation must still be blocked waiting for the
        // lease the reassignment is holding -- it should never reach dispatch, so a short-lived token
        // times it out rather than it completing or failing for any other reason.
        using var sendCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var sendAct = () => orchestrator.SendMessageAsync(
            "conn1", "c1", Guid.NewGuid(), "Hello", "user1", null, sendCts.Token);

        await sendAct.Should().ThrowAsync<OperationCanceledException>(
            "a concurrent turn must block on the reassignment's held lease, not race past it");
        _mediator.Verify(
            m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()), Times.Never,
            "the blocked turn must never reach dispatch while the reassignment still holds the lease");

        releaseReassignment.SetResult();
        var updated = await reassignTask;

        updated!.AgentName.Should().Be("new-agent");
        _agentCache.Verify(c => c.Evict("c1"), Times.Once);
    }

    /// <summary>
    /// Proves the fix for a second race a later review round caught: even with the write and
    /// eviction synchronized (the test above), a turn that captured its dispatch agent name BEFORE
    /// either lease was contested could still win the lease after the reassignment released it, and
    /// dispatch -- and re-cache -- against that stale name, undoing the eviction and reproducing the
    /// original persistent-staleness bug one turn later. <see cref="ConversationOrchestrator"/>
    /// closes this by re-reading <c>AgentName</c> fresh once <c>SendMessageAsync</c> actually holds
    /// the lease, rather than trusting the value read before the wait for the lease began.
    /// </summary>
    [Fact]
    public async Task SendMessage_LosesLeaseRaceToReassignment_DispatchesToTheNewAgentNotTheStaleOne()
    {
        var oldRecord = new ConversationRecord("c1", "old-agent", "user1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);
        var newRecord = oldRecord with { AgentName = "new-agent" };

        // Reflects the ACTUAL write, not call order: GetAsync returns old-agent until the
        // reassignment's store write genuinely happens, then new-agent for every call after. A
        // naive "first call gets old, rest get new" stub would pass or fail on scheduling luck
        // rather than proving anything about real interleaving.
        var written = false;
        _store.Setup(s => s.GetAsync("c1", "user1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => written ? newRecord : oldRecord);
        _store.Setup(s => s.GetHistoryForDispatch("c1", "user1", 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConversationMessage>());
        _obsStore.Setup(s => s.StartSessionAsync("c1", It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());

        string? dispatchedAgentName = null;
        _mediator.Setup(m => m.Send(It.IsAny<ExecuteAgentTurnCommand>(), It.IsAny<CancellationToken>()))
            .Returns((ExecuteAgentTurnCommand cmd, CancellationToken _) =>
            {
                dispatchedAgentName = cmd.AgentName;
                return Task.FromResult(new AgentTurnResult { Success = true, Response = "ok", UpdatedHistory = [] });
            });

        var reassignmentEntered = new TaskCompletionSource();
        var releaseReassignment = new TaskCompletionSource();
        _store.Setup(s => s.ReassignAgentAsync("c1", "user1", "new-agent", It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                reassignmentEntered.SetResult();
                await releaseReassignment.Task;
                written = true;
                return newRecord;
            });

        var orchestrator = CreateOrchestrator();

        // Reassignment goes first and holds the lease -- deterministically, not by scheduling luck.
        var reassignTask = orchestrator.ReassignAgentAsync("c1", "user1", "new-agent", CancellationToken.None);
        await reassignmentEntered.Task;

        // SendMessageAsync's pre-lease read happens now, synchronously, before it ever contends for
        // the lease -- `written` is still false, so it captures "old-agent", exactly simulating a
        // turn that started before the reassignment did. Its own lease acquisition then blocks,
        // because the reassignment above is still holding it.
        var sendTask = orchestrator.SendMessageAsync(
            "conn1", "c1", Guid.NewGuid(), "Hello", "user1", null, CancellationToken.None);

        releaseReassignment.SetResult();
        var reassigned = await reassignTask;
        reassigned!.AgentName.Should().Be("new-agent");

        var outcome = await sendTask;

        outcome.Success.Should().BeTrue();
        dispatchedAgentName.Should().Be("new-agent",
            "the turn must dispatch using the agent name read fresh once it holds the lease, not the " +
            "one it captured before the lease was ever contested");
        _agentCache.Verify(c => c.Evict("c1"), Times.Once,
            "the reassignment's own eviction must not be undone by the racing turn re-caching the old agent");
    }
}
