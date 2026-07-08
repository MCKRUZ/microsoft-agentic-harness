using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.MediatRBehaviors;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Microsoft.Extensions.DependencyInjection;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Config.AI;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.MediatRBehaviors;

public class KnowledgeExtractionBehaviorTests
{
    // Deadline for polling the fire-and-forget background extraction. Generous because the work
    // runs on the thread pool (Task.Run) and a CI machine under a parallel test load can delay
    // scheduling far past a tight deadline; polling means passing runs still finish in ~10ms.
    private static readonly TimeSpan ExtractionTimeout = TimeSpan.FromSeconds(30);

    private readonly Mock<IConversationFactExtractor> _mockExtractor = new();
    private readonly Mock<IKnowledgeMemory> _mockMemory = new();
    private readonly KnowledgeBridgeConfig _config;

    public KnowledgeExtractionBehaviorTests()
    {
        _config = new KnowledgeBridgeConfig { Enabled = true };
    }

    [Fact]
    public async Task Handle_NonAgentTurnRequest_PassesThrough()
    {
        var behavior = CreateBehavior<NonAgentRequest, string>();

        var result = await behavior.Handle(
            new NonAgentRequest(),
            () => Task.FromResult("passthrough"),
            CancellationToken.None);

        result.Should().Be("passthrough");
        _mockExtractor.Verify(
            e => e.ExtractAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_Disabled_SkipsExtraction()
    {
        _config.Enabled = false;
        var behavior = CreateAgentTurnBehavior();
        var command = CreateCommand("analyze this code");
        var response = CreateSuccessResponse("Here's my analysis...");

        var result = await behavior.Handle(command, () => Task.FromResult(response), CancellationToken.None);

        result.Should().BeSameAs(response);
        _mockExtractor.Verify(
            e => e.ExtractAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_FailedTurn_SkipsExtraction()
    {
        var behavior = CreateAgentTurnBehavior();
        var command = CreateCommand("do something");
        var response = new AgentTurnResult
        {
            Success = false,
            Response = "",
            UpdatedHistory = [],
            Error = "Agent error"
        };

        var result = await behavior.Handle(command, () => Task.FromResult(response), CancellationToken.None);

        result.Should().BeSameAs(response);
        _mockExtractor.Verify(
            e => e.ExtractAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_EmptyResponse_SkipsExtraction()
    {
        var behavior = CreateAgentTurnBehavior();
        var command = CreateCommand("hello");
        var response = CreateSuccessResponse("");

        var result = await behavior.Handle(command, () => Task.FromResult(response), CancellationToken.None);

        result.Should().BeSameAs(response);
        _mockExtractor.Verify(
            e => e.ExtractAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_SuccessfulTurn_ExtractsAndRemembersFacts()
    {
        var facts = new List<ConversationFact>
        {
            new() { Key = "conv-1:3:0", Content = "User prefers PostgreSQL", EntityType = "Preference", Confidence = 0.9 },
            new() { Key = "conv-1:3:1", Content = "Deadline is June 15", EntityType = "Decision", Confidence = 0.85 }
        };

        _mockExtractor
            .Setup(e => e.ExtractAsync("use PostgreSQL", "Noted.", "conv-1", 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(facts);

        var behavior = CreateAgentTurnBehavior();
        var command = CreateCommand("use PostgreSQL", "conv-1", 3);
        var response = CreateSuccessResponse("Noted.");

        var result = await behavior.Handle(command, () => Task.FromResult(response), CancellationToken.None);

        result.Should().BeSameAs(response);

        // The behavior persists facts on a fire-and-forget background task, so poll the mock
        // until both facts land (or fail loudly on timeout) rather than racing a fixed delay.
        await WaitForAsync(
            () => MemoryInvocationCount() == 2,
            ExtractionTimeout);

        _mockMemory.Verify(m => m.RememberAsync("conv-1:3:0", "User prefers PostgreSQL", "Preference", It.IsAny<CancellationToken>()), Times.Once);
        _mockMemory.Verify(m => m.RememberAsync("conv-1:3:1", "Deadline is June 15", "Decision", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_ExtractorThrows_BackgroundFailureIsAbsorbedAndPersistenceSkipped()
    {
        var extractorInvocations = 0;
        _mockExtractor
            .Setup(e => e.ExtractAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() => Interlocked.Increment(ref extractorInvocations))
            .ThrowsAsync(new InvalidOperationException("LLM down"));

        var behavior = CreateAgentTurnBehavior();
        var command = CreateCommand("analyze this");
        var response = CreateSuccessResponse("Analysis complete.");

        // The synchronous path must return the agent response immediately — extraction is
        // fire-and-forget, so a throwing extractor must never surface here.
        var result = await behavior.Handle(command, () => Task.FromResult(response), CancellationToken.None);
        result.Should().BeSameAs(response);

        // Drive the assertion against the background task actually running: wait until the
        // extractor has been invoked (proving the fire-and-forget body executed and threw),
        // then prove the throw short-circuited persistence. Without polling, an assertion of
        // "extractor was called" would be vacuous because the task may not have started yet.
        await WaitForAsync(
            () => Volatile.Read(ref extractorInvocations) == 1,
            ExtractionTimeout);

        // The extractor threw before yielding any facts, so nothing should be persisted, and
        // the behavior's catch block must have absorbed the exception (no unobserved fault,
        // no rethrow): the test process is still alive and RememberAsync was never reached.
        _mockMemory.Verify(
            m => m.RememberAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_RememberAsyncThrows_ContinuesWithRemainingFacts()
    {
        var facts = new List<ConversationFact>
        {
            new() { Key = "conv-1:1:0", Content = "Fact A", Confidence = 0.9 },
            new() { Key = "conv-1:1:1", Content = "Fact B", Confidence = 0.85 }
        };

        _mockExtractor
            .Setup(e => e.ExtractAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(facts);

        _mockMemory
            .Setup(m => m.RememberAsync("conv-1:1:0", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("graph full"));

        var behavior = CreateAgentTurnBehavior();
        var command = CreateCommand("msg", "conv-1", 1);
        var response = CreateSuccessResponse("resp");

        await behavior.Handle(command, () => Task.FromResult(response), CancellationToken.None);

        // Poll until the second fact's persistence is attempted (the first fact's RememberAsync
        // throws); a fixed delay would race the background task under CI load.
        await WaitForAsync(
            () => SecondFactRemembered(),
            ExtractionTimeout);

        // Second fact should still be remembered despite first one throwing
        _mockMemory.Verify(m => m.RememberAsync("conv-1:1:1", "Fact B", "Fact", It.IsAny<CancellationToken>()), Times.Once);
    }

    // --- Helpers ---

    /// <summary>
    /// Polls <paramref name="predicate"/> until it returns <c>true</c> or <paramref name="timeout"/>
    /// elapses, throwing <see cref="TimeoutException"/> on timeout. Replaces fixed delays so the
    /// fire-and-forget background extraction is awaited deterministically rather than raced.
    /// </summary>
    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(10);
        }

        throw new TimeoutException(
            $"Predicate did not become true within {timeout.TotalMilliseconds}ms.");
    }

    /// <summary>
    /// Counts how many times <see cref="IKnowledgeMemory.RememberAsync"/> has been invoked on the
    /// mock so far. Reads Moq's thread-safe invocation log directly so it can be polled without
    /// triggering the throwing behavior of <c>Verify</c> mid-wait.
    /// </summary>
    private int MemoryInvocationCount() =>
        _mockMemory.Invocations.Count(i => i.Method.Name == nameof(IKnowledgeMemory.RememberAsync));

    /// <summary>
    /// Returns <c>true</c> once <see cref="IKnowledgeMemory.RememberAsync"/> has been invoked for the
    /// second fact's key, used to poll for completion of the resilient persistence loop.
    /// </summary>
    private bool SecondFactRemembered() =>
        _mockMemory.Invocations.Any(i =>
            i.Method.Name == nameof(IKnowledgeMemory.RememberAsync) &&
            i.Arguments.Count > 0 &&
            (i.Arguments[0] as string) == "conv-1:1:1");

    // The behavior resolves IConversationFactExtractor and IKnowledgeMemory from a fresh DI
    // scope (created per background extraction). Build a scope factory whose provider returns
    // the same mocks so the existing assertions on _mockExtractor/_mockMemory still hold.
    private IServiceScopeFactory BuildScopeFactory()
    {
        var provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(IConversationFactExtractor))).Returns(_mockExtractor.Object);
        provider.Setup(p => p.GetService(typeof(IKnowledgeMemory))).Returns(_mockMemory.Object);

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

    private KnowledgeExtractionBehavior<TRequest, TResponse> CreateBehavior<TRequest, TResponse>()
        where TRequest : notnull
    {
        return new KnowledgeExtractionBehavior<TRequest, TResponse>(
            BuildScopeFactory(),
            BuildAmbientScope(),
            Options.Create(_config),
            NullLogger<KnowledgeExtractionBehavior<TRequest, TResponse>>.Instance);
    }

    private KnowledgeExtractionBehavior<ExecuteAgentTurnCommand, AgentTurnResult> CreateAgentTurnBehavior()
    {
        return new KnowledgeExtractionBehavior<ExecuteAgentTurnCommand, AgentTurnResult>(
            BuildScopeFactory(),
            BuildAmbientScope(),
            Options.Create(_config),
            NullLogger<KnowledgeExtractionBehavior<ExecuteAgentTurnCommand, AgentTurnResult>>.Instance);
    }

    private static ExecuteAgentTurnCommand CreateCommand(
        string userMessage, string conversationId = "conv-1", int turnNumber = 1) =>
        new()
        {
            AgentName = "test-agent",
            UserMessage = userMessage,
            ConversationId = conversationId,
            TurnNumber = turnNumber
        };

    private static AgentTurnResult CreateSuccessResponse(string response) =>
        new()
        {
            Success = true,
            Response = response,
            UpdatedHistory = []
        };

    // Test-local request type that is NOT ExecuteAgentTurnCommand
    private record NonAgentRequest : IRequest<string>;
}
