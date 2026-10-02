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

    private ExecuteAgentTurnCommandHandler CreateHandler(LlmUsageCapture capture)
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
            new Mock<IObservabilityStore>().Object,
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

    private static ExecuteAgentTurnCommand Command() => new()
    {
        AgentName = "TestAgent",
        UserMessage = "Hello",
        ConversationHistory = [],
        TurnNumber = 1,
    };

    [Fact]
    public async Task Handle_InternalFailureAfterSpending_ReportsWhatTheTurnSpent()
    {
        ArrangeAgentThatSpendsThenFails(new InvalidOperationException("boom"));

        var result = await CreateHandler(CreateCapture()).Handle(Command(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorKind.Should().Be(AgentTurnErrorKind.Internal);
        result.InputTokens.Should().Be(900);
        result.OutputTokens.Should().Be(100);
        result.CacheRead.Should().Be(25);
        result.CacheWrite.Should().Be(5);
    }

    [Fact]
    public async Task Handle_ConfigurationFailureAfterSpending_ReportsWhatTheTurnSpent()
    {
        ArrangeAgentThatSpendsThenFails(new AiProviderNotConfiguredException("no endpoint"));

        var result = await CreateHandler(CreateCapture()).Handle(Command(), CancellationToken.None);

        result.ErrorKind.Should().Be(AgentTurnErrorKind.Configuration);
        result.InputTokens.Should().Be(900);
        result.OutputTokens.Should().Be(100);
    }

    [Fact]
    public async Task Handle_CancelledAfterSpending_ReportsWhatTheTurnSpent()
    {
        ArrangeAgentThatSpendsThenFails(new OperationCanceledException());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await CreateHandler(CreateCapture()).Handle(Command(), cts.Token);

        result.ErrorKind.Should().Be(AgentTurnErrorKind.Cancelled);
        result.InputTokens.Should().Be(900);
        result.OutputTokens.Should().Be(100);
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
