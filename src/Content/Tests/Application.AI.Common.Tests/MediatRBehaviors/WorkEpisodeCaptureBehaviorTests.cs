using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.WorkMemory;
using Application.AI.Common.MediatRBehaviors;
using Application.Core.CQRS.Agents.ExecuteAgentTurn;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.WorkMemory;
using Domain.Common;
using Domain.Common.Config;
using Domain.Common.Config.AI.HarmonicMemory;
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
    // Deadline for polling the fire-and-forget background capture. Generous because the capture
    // runs on the thread pool (Task.Run) and a CI machine under a parallel test load can delay
    // scheduling far past a tight deadline; polling means passing runs still finish in ~10ms.
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(30);

    private readonly Mock<IWorkEpisodeStore> _store = new();
    private readonly Mock<IEpisodicSegmentStore> _segmentStore = new();
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

        await WaitForAsync(() => CountOf(captured) == 1, CaptureTimeout);

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

        await WaitForAsync(() => CountOf(captured) == 1, CaptureTimeout);
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

        await WaitForAsync(() => CountOf(captured) == 1, CaptureTimeout);
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

        await WaitForAsync(() => CountOf(captured) == 1, CaptureTimeout);
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
        await WaitForAsync(() => Volatile.Read(ref attempts) == 1, CaptureTimeout);
    }

    // --- Harmonic episodic-segment capture (shared turn-boundary seam) ---

    [Fact]
    public async Task Handle_HarmonicEnabled_CapturesRawSegmentCrossLinkedToEpisode()
    {
        _appConfig.AI.HarmonicMemory.Mode = HarmonicMemoryMode.AbstractOnly;
        var episodes = SetupCapture();
        var segments = SetupSegmentCapture();
        var behavior = CreateAgentTurnBehavior();
        // Response longer than the WorkEpisode truncation cap (2000) — the segment must keep it raw.
        var response = CreateResult(success: true, response: new string('y', 5000));

        await behavior.Handle(CreateCommand("the task", "conv-7", 3), () => Task.FromResult(response), CancellationToken.None);

        await WaitForAsync(() => CountOf(episodes) == 1 && CountOf(segments) == 1, CaptureTimeout);

        var episode = episodes.Single();
        var segment = segments.Single();
        segment.EpisodeId.Should().Be(episode.EpisodeId, "the segment cross-links to the same turn's work episode");
        segment.ConversationId.Should().Be("conv-7");
        segment.TurnNumber.Should().Be(3);
        segment.CreatedAt.Should().Be(episode.CreatedAt, "both records share the turn-completion timestamp");
        segment.SegmentId.Should().NotBe(Guid.Empty);
        // Raw + untruncated — the whole 5000-char response survives, unlike the truncated episode summary.
        segment.Content.Should().Contain(new string('y', 5000)).And.Contain("the task");
        episode.ResponseSummary.Length.Should().BeLessThan(5000, "the work episode is still truncated");
    }

    [Fact]
    public async Task Handle_HarmonicOnly_WorkMemoryOff_CapturesSegmentNotEpisode()
    {
        _appConfig.AI.WorkMemory.Enabled = false;
        _appConfig.AI.HarmonicMemory.Mode = HarmonicMemoryMode.Full;
        SetupCapture();
        var segments = SetupSegmentCapture();
        var behavior = CreateAgentTurnBehavior();
        var response = CreateResult(success: true, response: "said");

        await behavior.Handle(CreateCommand("q"), () => Task.FromResult(response), CancellationToken.None);

        await WaitForAsync(() => CountOf(segments) == 1, CaptureTimeout);
        // The episode is persisted before the segment in PersistAsync; once the segment lands, a
        // work-episode write (had it been enabled) would already have run. It must not have.
        _store.Verify(s => s.SaveAsync(It.IsAny<WorkEpisode>(), It.IsAny<CancellationToken>()), Times.Never);
        // No episode was persisted, so the segment's cross-link is null (correlation is by conversation+turn).
        segments.Single().EpisodeId.Should().BeNull();
    }

    [Fact]
    public async Task Handle_BothDisabled_SkipsBothCaptures()
    {
        _appConfig.AI.WorkMemory.Enabled = false;
        _appConfig.AI.HarmonicMemory.Mode = HarmonicMemoryMode.Off;
        var behavior = CreateAgentTurnBehavior();
        var response = CreateResult(success: true, response: "x");

        var result = await behavior.Handle(CreateCommand("hi"), () => Task.FromResult(response), CancellationToken.None);

        result.Should().BeSameAs(response);
        _store.Verify(s => s.SaveAsync(It.IsAny<WorkEpisode>(), It.IsAny<CancellationToken>()), Times.Never);
        _segmentStore.Verify(s => s.SaveAsync(It.IsAny<EpisodicSegment>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_EpisodeStoreThrows_SegmentStillPersisted()
    {
        // Both subsystems on. The episode write throws; the episodic segment must still be captured —
        // the two persists are independent.
        _appConfig.AI.HarmonicMemory.Mode = HarmonicMemoryMode.AbstractOnly;
        _store
            .Setup(s => s.SaveAsync(It.IsAny<WorkEpisode>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("episode store down"));
        var segments = SetupSegmentCapture();
        var behavior = CreateAgentTurnBehavior();
        var response = CreateResult(success: true, response: "said");

        await behavior.Handle(CreateCommand("q"), () => Task.FromResult(response), CancellationToken.None);

        await WaitForAsync(() => CountOf(segments) == 1, CaptureTimeout);
    }

    [Fact]
    public void BuildSegment_CrossLinksToEpisodeAndKeepsRawContent()
    {
        var behavior = CreateAgentTurnBehavior();
        var request = CreateCommand("what is x", "conv-2", 5);
        var result = CreateResult(success: true, response: "x is y");
        var episodeId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;

        var segment = behavior.BuildSegment(request, result, episodeId, createdAt);

        segment.EpisodeId.Should().Be(episodeId);
        segment.AgentId.Should().Be("test-agent");
        segment.ConversationId.Should().Be("conv-2");
        segment.TurnNumber.Should().Be(5);
        segment.CreatedAt.Should().Be(createdAt);
        segment.SegmentId.Should().NotBe(Guid.Empty);
        segment.Content.Should().Contain("what is x").And.Contain("x is y");
    }

    // --- Helpers ---

    private List<EpisodicSegment> SetupSegmentCapture()
    {
        var captured = new List<EpisodicSegment>();
        _segmentStore
            .Setup(s => s.SaveAsync(It.IsAny<EpisodicSegment>(), It.IsAny<CancellationToken>()))
            .Callback<EpisodicSegment, CancellationToken>((s, _) =>
            {
                lock (captured) { captured.Add(s); }
            })
            .ReturnsAsync(Result.Success());
        return captured;
    }

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

    /// <summary>
    /// Reads the capture list's count under the same lock the mock callbacks take when adding,
    /// so poll predicates never race the background writer with an unsynchronized read.
    /// </summary>
    private static int CountOf<T>(List<T> items)
    {
        lock (items)
        {
            return items.Count;
        }
    }

    private IServiceScopeFactory BuildScopeFactory()
    {
        var provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(IWorkEpisodeStore))).Returns(_store.Object);
        provider.Setup(p => p.GetService(typeof(IEpisodicSegmentStore))).Returns(_segmentStore.Object);

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
