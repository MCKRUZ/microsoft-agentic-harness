using Application.AI.Common.Interfaces.Routing;
using Application.AI.Common.Prompts.Exceptions;
using Application.AI.Common.Prompts.Interfaces;
using Application.AI.Common.Prompts.Models;
using Domain.AI.Learnings;
using Domain.AI.Prompts;
using Domain.AI.Routing.Models;
using Domain.AI.WorkMemory;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.WorkMemory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.WorkMemory;

public class LlmWorkEpisodeSynthesizerTests
{
    private readonly Mock<IModelRouter> _mockRouter = new();
    private readonly Mock<IChatClient> _mockClient = new();
    private readonly Mock<IPromptRegistry> _mockRegistry = new();
    private readonly Mock<IPromptRenderer> _mockRenderer = new();
    private readonly Mock<IPromptUsageRecorder> _mockRecorder = new();
    private readonly LlmWorkEpisodeSynthesizer _sut;

    public LlmWorkEpisodeSynthesizerTests()
    {
        var routingDecision = new ModelRoutingDecision
        {
            SelectedTier = new ModelTier
            {
                Name = "economy",
                ClientType = Domain.Common.Config.AI.AIAgentFrameworkClientType.OpenAI,
                DeploymentName = "gpt-4o-mini",
                EstimatedCostPer1KTokens = 0.00015m
            },
            Client = _mockClient.Object,
            Complexity = Domain.AI.Routing.Enums.TaskComplexity.Trivial,
            Source = Domain.AI.Routing.Enums.ClassificationSource.Heuristic,
            Confidence = 1.0
        };

        _mockRouter
            .Setup(r => r.RouteOperationAsync("work_episode_synthesis", It.IsAny<CancellationToken>()))
            .ReturnsAsync(routingDecision);

        var descriptor = new PromptDescriptor
        {
            Name = "work-episode-synthesizer",
            Version = new PromptVersion(1, 0),
            ContentHash = "deadbeef",
            Body = "Distill <episodes>{{episodes}}</episodes>",
        };

        _mockRegistry
            .Setup(r => r.GetLatestAsync("work-episode-synthesizer", It.IsAny<CancellationToken>()))
            .ReturnsAsync(descriptor);

        _mockRenderer
            .Setup(r => r.RenderAsync(
                It.IsAny<PromptDescriptor>(),
                It.IsAny<IReadOnlyDictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PromptDescriptor d, IReadOnlyDictionary<string, object?> _, CancellationToken __)
                => new RenderedPrompt { Source = d, Body = "rendered-prompt-body" });

        _mockRecorder
            .Setup(r => r.RecordAsync(
                It.IsAny<PromptDescriptor>(),
                It.IsAny<PromptUsageContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PromptDescriptor d, PromptUsageContext c, CancellationToken _) => new PromptUsageRecord
            {
                Descriptor = d,
                CaseId = c.CaseId,
                MetricKey = c.MetricKey,
                RecordedAtUtc = DateTimeOffset.UtcNow,
            });

        _sut = new LlmWorkEpisodeSynthesizer(
            _mockRouter.Object,
            _mockRegistry.Object,
            _mockRenderer.Object,
            _mockRecorder.Object,
            NullLogger<LlmWorkEpisodeSynthesizer>.Instance);
    }

    [Fact]
    public async Task SynthesizeAsync_EmptyBatch_ReturnsEmptyAndSkipsLlm()
    {
        var result = await _sut.SynthesizeAsync([], CancellationToken.None);

        result.Should().BeEmpty();
        _mockRegistry.Verify(
            r => r.GetLatestAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockClient.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SynthesizeAsync_PromptUnavailable_ReturnsEmptyAndSkipsLlm()
    {
        _mockRegistry
            .Setup(r => r.GetLatestAsync("work-episode-synthesizer", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PromptRegistryUnavailableException(
                "work-episode-synthesizer", "backend offline", new IOException("blip")));

        var result = await _sut.SynthesizeAsync([Episode()], CancellationToken.None);

        result.Should().BeEmpty();
        _mockClient.VerifyNoOtherCalls();
        _mockRecorder.Verify(
            r => r.RecordAsync(It.IsAny<PromptDescriptor>(), It.IsAny<PromptUsageContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SynthesizeAsync_RecordsPromptUsageWithSynthesisMetricKey()
    {
        SetupLlmResponse("[]");

        await _sut.SynthesizeAsync([Episode(), Episode()], CancellationToken.None);

        _mockRecorder.Verify(
            r => r.RecordAsync(
                It.Is<PromptDescriptor>(d => d.Name == "work-episode-synthesizer"),
                It.Is<PromptUsageContext>(c => c.MetricKey == "work_episode_synthesis"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SynthesizeAsync_ValidJson_ReturnsMappedLessons()
    {
        SetupLlmResponse("""
            [
              {"content": "Build from the worktree path, not main root", "category": "ToolUsagePattern", "confidence": 0.9},
              {"content": "Always verify tests before reporting done", "category": "InstructionUpdate", "confidence": 0.8}
            ]
            """);

        var result = await _sut.SynthesizeAsync([Episode()], CancellationToken.None);

        result.Should().HaveCount(2);
        result[0].Content.Should().Be("Build from the worktree path, not main root");
        result[0].Category.Should().Be(LearningCategory.ToolUsagePattern);
        result[0].Confidence.Should().Be(0.9);
        result[1].Category.Should().Be(LearningCategory.InstructionUpdate);
    }

    [Fact]
    public async Task SynthesizeAsync_UnknownCategory_SkipsLesson()
    {
        SetupLlmResponse("""
            [
              {"content": "good lesson", "category": "DomainKnowledge", "confidence": 0.9},
              {"content": "uncategorizable", "category": "NotARealCategory", "confidence": 0.9}
            ]
            """);

        var result = await _sut.SynthesizeAsync([Episode()], CancellationToken.None);

        result.Should().ContainSingle();
        result[0].Category.Should().Be(LearningCategory.DomainKnowledge);
    }

    [Fact]
    public async Task SynthesizeAsync_BlankContent_SkipsLesson()
    {
        SetupLlmResponse("""
            [
              {"content": "   ", "category": "DomainKnowledge", "confidence": 0.9}
            ]
            """);

        var result = await _sut.SynthesizeAsync([Episode()], CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task SynthesizeAsync_OutOfRangeConfidence_IsClamped()
    {
        SetupLlmResponse("""
            [
              {"content": "over", "category": "FactualCorrection", "confidence": 1.5},
              {"content": "under", "category": "FactualCorrection", "confidence": -0.3}
            ]
            """);

        var result = await _sut.SynthesizeAsync([Episode()], CancellationToken.None);

        result.Should().HaveCount(2);
        result[0].Confidence.Should().Be(1.0);
        result[1].Confidence.Should().Be(0.0);
    }

    [Fact]
    public async Task SynthesizeAsync_MalformedJson_ReturnsEmpty()
    {
        SetupLlmResponse("not json at all");

        var result = await _sut.SynthesizeAsync([Episode()], CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task SynthesizeAsync_JsonWrappedInMarkdown_ParsesCorrectly()
    {
        SetupLlmResponse("""
            ```json
            [
              {"content": "fenced lesson", "category": "StylePreference", "confidence": 0.85}
            ]
            ```
            """);

        var result = await _sut.SynthesizeAsync([Episode()], CancellationToken.None);

        result.Should().ContainSingle();
        result[0].Content.Should().Be("fenced lesson");
    }

    [Fact]
    public async Task SynthesizeAsync_LlmThrows_ReturnsEmpty()
    {
        _mockClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("rate limited"));

        var result = await _sut.SynthesizeAsync([Episode()], CancellationToken.None);

        result.Should().BeEmpty();
    }

    private static WorkEpisode Episode() => new()
    {
        EpisodeId = Guid.NewGuid(),
        AgentId = "agent-1",
        ConversationId = "conv-1",
        TurnNumber = 1,
        UserMessage = "do the thing",
        ResponseSummary = "did the thing",
        Outcome = EpisodeOutcome.Success,
        InputTokens = 100,
        OutputTokens = 50,
        CreatedAt = DateTimeOffset.UtcNow
    };

    private void SetupLlmResponse(string responseText)
    {
        var chatResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText));
        _mockClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IList<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(chatResponse);
    }
}
