using Application.AI.Common.Categorization;
using Application.AI.Common.Exceptions;
using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Notifications;
using Application.AI.Common.Services;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Application.Core.Tests.Fakes;
using Application.Core.Tests.Helpers;
using Domain.AI.Skills;
using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS;

/// <summary>
/// #778: a turn that fails or is cancelled has still spent whatever its model calls cost, and the
/// conversation budget is charged from the turn result. These drive the real
/// <see cref="LlmUsageCapture"/> (the sibling fixture's mock returns an empty snapshot, which cannot
/// tell a handler that reads it on failure from one that does not).
/// </summary>
public sealed class ExecuteAgentTurnCommandHandler_FailedTurnUsageTests
{
    private readonly Mock<IAgentConversationCache> _agentCache = new();

    private ExecuteAgentTurnCommandHandler CreateHandler(
        LlmUsageCapture capture, Mock<IObservabilityStore>? observability = null)
    {
        var registry = new Mock<IAgentMetadataRegistry>();
        registry.Setup(r => r.TryGet(It.IsAny<string>())).Returns((Domain.AI.Agents.AgentDefinition?)null);

        return new ExecuteAgentTurnCommandHandler(
            _agentCache.Object,
            Mock.Of<Application.AI.Common.Interfaces.Governance.IToolCallAdmissionPipeline>(
                p => p.GetTrace() == Domain.AI.Governance.GovernanceTrace.Empty),
            registry.Object,
            new Mock<ISkillMetadataRegistry>().Object,
            new Application.AI.Common.Services.Context.ConversationRegistrationTracker(),
            (observability ?? new Mock<IObservabilityStore>()).Object,
            capture,
            new DefaultContextSnapshotComputer(),
            new NullContextSnapshotNotifier(),
            TimeProvider.System,
            NullLogger<ExecuteAgentTurnCommandHandler>.Instance,
            new PassthroughToolCallReplayTreatment(),
            Mock.Of<Application.Core.Orchestration.Magentic.IMagenticAgentTurnRunner>());
    }

    private static LlmUsageCapture CreateCapture() =>
        new(Mock.Of<IOptionsMonitor<AppConfig>>(o => o.CurrentValue == new AppConfig()));

    private void ArrangeAgentThatSpendsThenFails(Exception failure)
    {
        var agent = new TestableAIAgent((_, _) =>
        {
            // The handler arms LlmUsageCapture.Current for the run, exactly as ObservabilityMiddleware
            // finds it when a real model call lands.
            LlmUsageCapture.Current!.Record(900, 100, 25, 5, "test-model");
            throw failure;
        });

        _agentCache
            .Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);
    }

    private static readonly Guid SessionId = Guid.NewGuid();

    private static ExecuteAgentTurnCommand Command() => new()
    {
        AgentName = "TestAgent",
        UserMessage = "Hello",
        ConversationHistory = [],
        TurnNumber = 1,
        ObservabilitySessionId = SessionId,
    };

    private static void VerifyFailedAssistantRow(Mock<IObservabilityStore> observability, Times times, string? error = null) =>
        observability.Verify(
            o => o.RecordMessageAsync(
                SessionId, 1, "assistant", "assistant_failed", It.Is<string?>(p => error == null || p == error),
                It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<decimal>(),
                It.IsAny<decimal>(), It.IsAny<string[]?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            times);

    [Theory]
    [InlineData(AgentTurnErrorKind.Internal)]
    [InlineData(AgentTurnErrorKind.Configuration)]
    [InlineData(AgentTurnErrorKind.Cancelled)]
    public async Task Handle_FailureAfterSpending_ReportsWhatTheTurnSpent(AgentTurnErrorKind kind)
    {
        ArrangeAgentThatSpendsThenFails(kind switch
        {
            AgentTurnErrorKind.Configuration => new AiProviderNotConfiguredException("no endpoint"),
            AgentTurnErrorKind.Cancelled => new OperationCanceledException(),
            _ => new InvalidOperationException("boom"),
        });
        using var cts = new CancellationTokenSource();
        if (kind == AgentTurnErrorKind.Cancelled)
            await cts.CancelAsync();

        var result = await CreateHandler(CreateCapture()).Handle(Command(), cts.Token);

        result.Success.Should().BeFalse();
        result.ErrorKind.Should().Be(kind);
        result.InputTokens.Should().Be(900);
        result.OutputTokens.Should().Be(100);
        result.CacheRead.Should().Be(25);
        result.CacheWrite.Should().Be(5);
    }

    [Fact]
    public async Task Handle_RunSucceedsButRecordingTheTurnThrows_StillReportsWhatTheRunSpent()
    {
        // The run completed and drained the capture; a step after it (here the observability write)
        // then throws. Reading the capture again at that point would find it empty and report zero for a
        // turn that was fully paid for.
        var agent = new TestableAIAgent((_, _) =>
        {
            LlmUsageCapture.Current!.Record(900, 100, 0, 0, "test-model");
            return Task.FromResult(new Microsoft.Agents.AI.AgentResponse(
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "ok")));
        });
        _agentCache
            .Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);
        var observability = new Mock<IObservabilityStore>();
        observability
            .Setup(o => o.RecordMessageAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), "assistant", It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<string[]?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("observability store unavailable"));

        var result = await CreateHandler(CreateCapture(), observability).Handle(Command(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.InputTokens.Should().Be(900);
        result.OutputTokens.Should().Be(100);
    }

    [Theory]
    [InlineData(AgentTurnErrorKind.Internal)]
    [InlineData(AgentTurnErrorKind.Cancelled)]
    public async Task Handle_FailureAfterSpending_WritesAFailedAssistantRowCarryingTheSpend(AgentTurnErrorKind kind)
    {
        // The session rollup and the budget both count this spend (#778, #780); the per-message rows are
        // what a dashboard drills into, so the turn has to be visible there too, marked as failed.
        ArrangeAgentThatSpendsThenFails(
            kind == AgentTurnErrorKind.Cancelled ? new OperationCanceledException() : new InvalidOperationException("boom"));
        var observability = new Mock<IObservabilityStore>();
        using var cts = new CancellationTokenSource();
        if (kind == AgentTurnErrorKind.Cancelled)
            await cts.CancelAsync();

        var result = await CreateHandler(CreateCapture(), observability).Handle(Command(), cts.Token);

        observability.Verify(
            o => o.RecordMessageAsync(
                SessionId, 1, "assistant", "assistant_failed", result.Error, "test-model",
                900, 100, 25, 5, It.IsAny<decimal>(), It.IsAny<decimal>(), null, result.Error,
                // Never the caller's token: a disconnect cancels it, and the row is for exactly that turn.
                It.Is<CancellationToken>(t => !t.CanBeCanceled)),
            Times.Once);
    }

    [Fact]
    public async Task Handle_FailureBeforeAnyModelCall_WritesNoFailedRow()
    {
        // Nothing was spent, so there is nothing to attribute — the same line the rollup draws.
        var agent = new TestableAIAgent((_, _) => throw new InvalidOperationException("boom"));
        _agentCache
            .Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);
        var observability = new Mock<IObservabilityStore>();

        await CreateHandler(CreateCapture(), observability).Handle(Command(), CancellationToken.None);

        VerifyFailedAssistantRow(observability, Times.Never());
    }

    [Fact]
    public async Task Handle_CompletedRowAlreadyWrittenThenALaterStepThrows_DoesNotWriteASecondRowForTheTurn()
    {
        // The completed turn's own row carries the spend. A failed row on top of it would double the turn
        // in any per-message total, while the rollup and the budget count it once.
        var agent = new TestableAIAgent((_, _) =>
        {
            LlmUsageCapture.Current!.Record(900, 100, 0, 0, "test-model");
            LlmUsageCapture.Current!.RecordToolCall("a_tool");
            return Task.FromResult(new Microsoft.Agents.AI.AgentResponse(
                new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "ok")));
        });
        _agentCache
            .Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<SkillAgentOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);
        var observability = new Mock<IObservabilityStore>();
        observability
            .Setup(o => o.RecordToolExecutionAsync(
                It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("observability store unavailable"));

        var result = await CreateHandler(CreateCapture(), observability).Handle(Command(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.InputTokens.Should().Be(900);
        VerifyFailedAssistantRow(observability, Times.Never());
    }

    [Fact]
    public async Task Handle_TheFailedRowCannotBeWritten_StillReturnsTheFailureWithItsSpend()
    {
        // Recording is for the dashboards; it must not replace the outcome the caller has to act on.
        ArrangeAgentThatSpendsThenFails(new InvalidOperationException("boom"));
        var observability = new Mock<IObservabilityStore>();
        observability
            .Setup(o => o.RecordMessageAsync(
                SessionId, 1, "assistant", "assistant_failed", It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<decimal>(),
                It.IsAny<decimal>(), It.IsAny<string[]?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("observability store unavailable"));

        var result = await CreateHandler(CreateCapture(), observability).Handle(Command(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorKind.Should().Be(AgentTurnErrorKind.Internal);
        result.InputTokens.Should().Be(900);
    }

    [Fact]
    public async Task Handle_FailedTurn_LeavesNothingBehindForTheNextTurnInTheSameScope()
    {
        // The capture is scoped and shared by every turn of a run. A failed turn that left its spend in
        // it would charge the next turn for it too.
        ArrangeAgentThatSpendsThenFails(new InvalidOperationException("boom"));
        var capture = CreateCapture();

        await CreateHandler(capture).Handle(Command(), CancellationToken.None);

        capture.TakeSnapshot().InputTokens.Should().Be(0);
    }
}
