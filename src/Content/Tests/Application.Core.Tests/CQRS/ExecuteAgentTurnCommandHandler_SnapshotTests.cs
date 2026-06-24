using Application.AI.Common.Categorization;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Notifications;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Application.Core.Tests.Helpers;
using Domain.AI.Context;
using Domain.AI.Skills;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS;

/// <summary>
/// Tests that the Foresight per-turn context-snapshot pipeline fires from
/// the turn handler and that notifier failures never fail the turn.
/// </summary>
public class ExecuteAgentTurnCommandHandler_SnapshotTests
{
    private readonly Mock<IAgentConversationCache> _agentCache = new();
    private readonly Mock<IAgentMetadataRegistry> _agentRegistry = new();
    private readonly Mock<IContextSnapshotNotifier> _notifier = new();
    private readonly Mock<IObservabilityStore> _store = new();

    private ExecuteAgentTurnCommandHandler BuildHandler(IContextSnapshotNotifier notifier)
    {
        _agentRegistry
            .Setup(r => r.TryGet(It.IsAny<string>()))
            .Returns((Domain.AI.Agents.AgentDefinition?)null);

        var usageCapture = new Mock<ILlmUsageCapture>();
        usageCapture
            .Setup(c => c.TakeSnapshot())
            .Returns(new LlmUsageSnapshot(
                InputTokens: 5_000,
                OutputTokens: 200,
                CacheRead: 0,
                CacheWrite: 0,
                Model: "test-model",
                CostUsd: 0m,
                CacheHitPct: 0m,
                ToolNames: Array.Empty<string>()));

        return new ExecuteAgentTurnCommandHandler(
            _agentCache.Object,
            new Mock<Application.AI.Common.Interfaces.Governance.IToolInvocationGovernor>().Object,
            new Mock<Application.AI.Common.Interfaces.Governance.IProgressEvaluator>().Object,
            _agentRegistry.Object,
            new Mock<ISkillMetadataRegistry>().Object,
            new Application.AI.Common.Services.Context.ConversationRegistrationTracker(),
            _store.Object,
            usageCapture.Object,
            new DefaultContextSnapshotComputer(),
            notifier,
            TimeProvider.System,
            NullLogger<ExecuteAgentTurnCommandHandler>.Instance);
    }

    private void SetupAgent(string response = "ok")
    {
        var agent = new TestableAIAgent(response);
        _agentCache
            .Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);
    }

    private static ExecuteAgentTurnCommand Command(string conv = "conv-1", int turn = 0) => new()
    {
        AgentName = "TestAgent",
        ConversationId = conv,
        UserMessage = "tell me a joke",
        ConversationHistory = [],
        TurnNumber = turn,
    };

    [Fact]
    public async Task Handle_OnSuccess_InvokesContextSnapshotNotifier_Once()
    {
        SetupAgent();
        var handler = BuildHandler(_notifier.Object);

        var result = await handler.Handle(Command(turn: 3), CancellationToken.None);

        result.Success.Should().BeTrue();
        _notifier.Verify(
            n => n.NotifyAsync(It.IsAny<ContextSnapshot>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_PassesConversationIdAndTurnIndexToSnapshot()
    {
        SetupAgent();
        ContextSnapshot? captured = null;
        _notifier
            .Setup(n => n.NotifyAsync(It.IsAny<ContextSnapshot>(), It.IsAny<CancellationToken>()))
            .Callback<ContextSnapshot, CancellationToken>((s, _) => captured = s)
            .Returns(Task.CompletedTask);

        var handler = BuildHandler(_notifier.Object);
        await handler.Handle(Command(conv: "conv-42", turn: 7), CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.ConversationId.Should().Be("conv-42");
        captured.TurnIndex.Should().Be(7);
        captured.TurnId.Should().Be("t-07");
    }

    [Fact]
    public async Task Handle_OnSuccess_PersistsSnapshotViaStore_Once()
    {
        SetupAgent();
        var handler = BuildHandler(_notifier.Object);

        var result = await handler.Handle(Command(turn: 3), CancellationToken.None);

        result.Success.Should().BeTrue();
        _store.Verify(
            s => s.RecordContextSnapshotAsync(
                It.Is<ContextSnapshot>(snap => snap.TurnIndex == 3 && snap.ConversationId == "conv-1"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_StorePersistThrows_DoesNotFailTurn()
    {
        SetupAgent("agent text");
        _store
            .Setup(s => s.RecordContextSnapshotAsync(It.IsAny<ContextSnapshot>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var handler = BuildHandler(_notifier.Object);
        var result = await handler.Handle(Command(), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Response.Should().Be("agent text");
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task Handle_NotifierThrows_DoesNotFailTurn()
    {
        SetupAgent("agent text");
        _notifier
            .Setup(n => n.NotifyAsync(It.IsAny<ContextSnapshot>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transport hiccup"));

        var handler = BuildHandler(_notifier.Object);
        var result = await handler.Handle(Command(), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Response.Should().Be("agent text");
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task Handle_LoadedItems_IncludeUserAndAssistantMessages()
    {
        SetupAgent("the chicken crossed the road");
        ContextSnapshot? captured = null;
        _notifier
            .Setup(n => n.NotifyAsync(It.IsAny<ContextSnapshot>(), It.IsAny<CancellationToken>()))
            .Callback<ContextSnapshot, CancellationToken>((s, _) => captured = s)
            .Returns(Task.CompletedTask);

        var handler = BuildHandler(_notifier.Object);
        await handler.Handle(Command(), CancellationToken.None);

        captured!.Loaded.Count.Should().BeGreaterThanOrEqualTo(2);
        captured.Loaded.Should().Contain(li => li.What == "User message");
        captured.Loaded.Should().Contain(li => li.What == "Assistant message");
        captured.Loaded.All(li => li.Category == ContextCategory.Messages).Should().BeTrue();
    }
}
