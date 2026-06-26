using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.WorkMemory;
using Application.AI.Common.MediatRBehaviors;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Domain.AI.WorkMemory;
using Domain.Common;
using Domain.Common.Config;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.MediatRBehaviors;

public sealed class WorkEpisodeCaptureBehaviorTests
{
    private readonly Mock<IWorkEpisodeStore> _store = new();
    private readonly AppConfig _appConfig = new();

    public WorkEpisodeCaptureBehaviorTests()
    {
        _appConfig.AI.WorkMemory.Enabled = true;
        _appConfig.AI.WorkMemory.ResponseSummaryMaxChars = 2000;
    }

    [Fact]
    public async Task Handle_NonAgentTurnRequest_PassesThroughWithoutCapture()
    {
        var behavior = CreateBehavior<NonAgentRequest, string>();

        var result = await behavior.Handle(
            new NonAgentRequest(),
            () => Task.FromResult("passthrough"),
            CancellationToken.None);

        result.Should().Be("passthrough");
        _store.Verify(s => s.SaveAsync(It.IsAny<WorkEpisode>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_Disabled_SkipsCapture()
    {
        _appConfig.AI.WorkMemory.Enabled = false;
        var behavior = CreateAgentTurnBehavior();
        var response = CreateResult(success: true, response: "done");

        var result = await behavior.Handle(CreateCommand("hi"), () => Task.FromResult(response), CancellationToken.None);

        result.Should().BeSameAs(response);
        _store.Verify(s => s.SaveAsync(It.IsAny<WorkEpisode>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_SuccessfulTurn_CapturesEpisodeWithOutcomeAndTokens()
    {
        var captured = SetupCapture();
        var behavior = CreateAgentTurnBehavior();
        var response = CreateResult(success: true, response: "the answer", inputTokens: 120, outputTokens: 45);

        await behavior.Handle(CreateCommand("the task", "conv-9", 4), () => Task.FromResult(response), CancellationToken.None);

        await WaitForAsync(() => captured.Count == 1, TimeSpan.FromSeconds(2));

        var episode = captured.Single();
        episode.AgentId.Should().Be("test-agent"); // ExecuteAgentTurnCommand.AgentId => AgentName
        episode.ConversationId.Should().Be("conv-9");
        episode.TurnNumber.Should().Be(4);
        episode.UserMessage.Should().Be("the task");
        episode.ResponseSummary.Should().Be("the answer");
        episode.Outcome.Should().Be(EpisodeOutcome.Success);
        episode.InputTokens.Should().Be(120);
        episode.OutputTokens.Should().Be(45);
        episode.TotalTokens.Should().Be(165);
        episode.EpisodeId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task Handle_FailedTurn_CapturesEpisodeWithFailureOutcome()
    {
        var captured = SetupCapture();
        var behavior = CreateAgentTurnBehavior();
        var response = CreateResult(success: false, response: "");

        await behavior.Handle(CreateCommand("do it"), () => Task.FromResult(response), CancellationToken.None);

        await WaitForAsync(() => captured.Count == 1, TimeSpan.FromSeconds(2));
        captured.Single().Outcome.Should().Be(EpisodeOutcome.Failure);
    }

    [Fact]
    public async Task Handle_LongResponse_TruncatesSummaryToConfiguredCap()
    {
        _appConfig.AI.WorkMemory.ResponseSummaryMaxChars = 10;
        var captured = SetupCapture();
        var behavior = CreateAgentTurnBehavior();
        var response = CreateResult(success: true, response: new string('x', 5000));

        await behavior.Handle(CreateCommand("summarize"), () => Task.FromResult(response), CancellationToken.None);

        await WaitForAsync(() => captured.Count == 1, TimeSpan.FromSeconds(2));
        captured.Single().ResponseSummary.Should().HaveLength(10);
    }

    [Fact]
    public async Task Handle_TruncationBoundaryOnSurrogatePair_DoesNotSplitThePair()
    {
        // Cap at 5 so the boundary lands exactly between the two halves of the 3rd emoji (each
        // emoji is a surrogate pair = 2 chars). Naive value[..5] would leave a lone high surrogate.
        _appConfig.AI.WorkMemory.ResponseSummaryMaxChars = 5;
        var captured = SetupCapture();
        var behavior = CreateAgentTurnBehavior();
        var response = CreateResult(success: true, response: string.Concat(Enumerable.Repeat("😀", 4)));

        await behavior.Handle(CreateCommand("emoji"), () => Task.FromResult(response), CancellationToken.None);

        await WaitForAsync(() => captured.Count == 1, TimeSpan.FromSeconds(2));
        var summary = captured.Single().ResponseSummary;
        summary.Should().HaveLength(4); // backed off one char to keep whole pairs
        char.IsHighSurrogate(summary[^1]).Should().BeFalse();
    }

    [Fact]
    public async Task Handle_StoreThrows_BackgroundFailureIsAbsorbed()
    {
        var attempts = 0;
        _store
            .Setup(s => s.SaveAsync(It.IsAny<WorkEpisode>(), It.IsAny<CancellationToken>()))
            .Callback(() => Interlocked.Increment(ref attempts))
            .ThrowsAsync(new InvalidOperationException("graph down"));

        var behavior = CreateAgentTurnBehavior();
        var response = CreateResult(success: true, response: "ok");

        // Synchronous path returns immediately — capture is fire-and-forget.
        var result = await behavior.Handle(CreateCommand("go"), () => Task.FromResult(response), CancellationToken.None);
        result.Should().BeSameAs(response);

        // Prove the background body ran (and threw) without faulting the test process.
        await WaitForAsync(() => Volatile.Read(ref attempts) == 1, TimeSpan.FromSeconds(2));
    }

    // --- Helpers ---

    private List<WorkEpisode> SetupCapture()
    {
        var captured = new List<WorkEpisode>();
        _store
            .Setup(s => s.SaveAsync(It.IsAny<WorkEpisode>(), It.IsAny<CancellationToken>()))
            .Callback<WorkEpisode, CancellationToken>((e, _) =>
            {
                lock (captured) { captured.Add(e); }
            })
            .ReturnsAsync(Result.Success());
        return captured;
    }

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(10);
        }
        throw new TimeoutException($"Predicate did not become true within {timeout.TotalMilliseconds}ms.");
    }

    private IServiceScopeFactory BuildScopeFactory()
    {
        var provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(IWorkEpisodeStore))).Returns(_store.Object);

        var scope = new Mock<IServiceScope>();
        scope.SetupGet(s => s.ServiceProvider).Returns(provider.Object);

        var factory = new Mock<IServiceScopeFactory>();
        factory.Setup(f => f.CreateScope()).Returns(scope.Object);
        return factory.Object;
    }

    private static IAmbientRequestScope BuildAmbientScope()
    {
        var ambient = new Mock<IAmbientRequestScope>();
        ambient.Setup(a => a.BeginScope(It.IsAny<IServiceProvider>())).Returns(Mock.Of<IDisposable>());
        return ambient.Object;
    }

    private WorkEpisodeCaptureBehavior<TRequest, TResponse> CreateBehavior<TRequest, TResponse>()
        where TRequest : notnull =>
        new(
            BuildScopeFactory(),
            BuildAmbientScope(),
            Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == _appConfig),
            TimeProvider.System,
            NullLogger<WorkEpisodeCaptureBehavior<TRequest, TResponse>>.Instance);

    private WorkEpisodeCaptureBehavior<ExecuteAgentTurnCommand, AgentTurnResult> CreateAgentTurnBehavior() =>
        CreateBehavior<ExecuteAgentTurnCommand, AgentTurnResult>();

    private static ExecuteAgentTurnCommand CreateCommand(
        string userMessage, string conversationId = "conv-1", int turnNumber = 1) =>
        new()
        {
            AgentName = "test-agent",
            UserMessage = userMessage,
            ConversationId = conversationId,
            TurnNumber = turnNumber
        };

    private static AgentTurnResult CreateResult(bool success, string response, int inputTokens = 0, int outputTokens = 0) =>
        new()
        {
            Success = success,
            Response = response,
            UpdatedHistory = [],
            InputTokens = inputTokens,
            OutputTokens = outputTokens
        };

    private sealed record NonAgentRequest : IRequest<string>;
}
