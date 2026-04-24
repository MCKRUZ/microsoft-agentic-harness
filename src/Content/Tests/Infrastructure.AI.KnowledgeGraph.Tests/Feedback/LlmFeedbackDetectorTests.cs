using Application.AI.Common.Interfaces.RAG;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.Feedback;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Feedback;

/// <summary>
/// Tests for <see cref="LlmFeedbackDetector"/> — positive/negative/no feedback
/// detection, JSON parsing, and error resilience.
/// </summary>
public sealed class LlmFeedbackDetectorTests
{
    private readonly Mock<IRagModelRouter> _modelRouter;
    private readonly Mock<IChatClient> _chatClient;
    private readonly LlmFeedbackDetector _detector;

    public LlmFeedbackDetectorTests()
    {
        _modelRouter = new Mock<IRagModelRouter>();
        _chatClient = new Mock<IChatClient>();
        _modelRouter
            .Setup(r => r.GetClientForOperation("feedback_detection"))
            .Returns(_chatClient.Object);

        _detector = new LlmFeedbackDetector(
            _modelRouter.Object,
            Mock.Of<ILogger<LlmFeedbackDetector>>());
    }

    [Fact]
    public async Task DetectFeedback_PositiveSignal_ReturnsFeedbackDetected()
    {
        SetupLlmResponse("""{"feedbackDetected": true, "feedbackScore": 5, "feedbackText": "User expressed satisfaction", "containsFollowupQuestion": false}""");

        var result = await _detector.DetectFeedbackAsync(
            "That's exactly what I needed, thanks!",
            "Here is the Azure documentation...");

        result.FeedbackDetected.Should().BeTrue();
        result.FeedbackScore.Should().Be(5);
        result.FeedbackText.Should().Contain("satisfaction");
        result.ContainsFollowupQuestion.Should().BeFalse();
    }

    [Fact]
    public async Task DetectFeedback_NegativeSignal_ReturnsLowScore()
    {
        SetupLlmResponse("""{"feedbackDetected": true, "feedbackScore": 1, "feedbackText": "User corrected the answer", "containsFollowupQuestion": false}""");

        var result = await _detector.DetectFeedbackAsync(
            "That's not right, I meant the other API",
            "Here is the Azure documentation...");

        result.FeedbackDetected.Should().BeTrue();
        result.FeedbackScore.Should().Be(1);
    }

    [Fact]
    public async Task DetectFeedback_NoFeedback_ReturnsFalse()
    {
        SetupLlmResponse("""{"feedbackDetected": false, "feedbackScore": 3}""");

        var result = await _detector.DetectFeedbackAsync("ok", "Here is info...");

        result.FeedbackDetected.Should().BeFalse();
    }

    [Fact]
    public async Task DetectFeedback_LlmThrows_ReturnsNoFeedbackGracefully()
    {
        _chatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Service unavailable"));

        var result = await _detector.DetectFeedbackAsync("test", "response");

        result.FeedbackDetected.Should().BeFalse();
        result.ContainsFollowupQuestion.Should().BeFalse();
    }

    [Fact]
    public async Task DetectFeedback_MalformedJson_ReturnsNoFeedback()
    {
        SetupLlmResponse("not valid json at all");

        var result = await _detector.DetectFeedbackAsync("test", "response");

        result.FeedbackDetected.Should().BeFalse();
    }

    [Fact]
    public async Task DetectFeedback_WithFollowup_SetsFlag()
    {
        SetupLlmResponse("""{"feedbackDetected": true, "feedbackScore": 4, "feedbackText": "Positive with follow-up", "containsFollowupQuestion": true}""");

        var result = await _detector.DetectFeedbackAsync(
            "Great, and what about the other endpoint?",
            "Here is the API spec...");

        result.ContainsFollowupQuestion.Should().BeTrue();
    }

    private void SetupLlmResponse(string responseText)
    {
        var chatResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText));
        _chatClient
            .Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(chatResponse);
    }
}
