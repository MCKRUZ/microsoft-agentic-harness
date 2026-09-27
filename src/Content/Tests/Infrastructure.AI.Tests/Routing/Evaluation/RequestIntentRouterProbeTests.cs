using Application.AI.Common.Interfaces.Routing;
using Domain.AI.Routing.Enums;
using Domain.AI.Routing.Models;
using FluentAssertions;
using Infrastructure.AI.Routing.Evaluation;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Routing.Evaluation;

public sealed class RequestIntentRouterProbeTests
{
    [Fact]
    public void Key_IsRequestIntent()
    {
        var sut = new RequestIntentRouterProbe(Mock.Of<IRequestIntentClassifier>());
        sut.Key.Should().Be("request_intent");
    }

    [Fact]
    public async Task ClassifyAsync_MapsIntentToLabel()
    {
        var classifier = ClassifierReturning(RequestIntent.CodeGeneration, confidence: 0.77);
        var sut = new RequestIntentRouterProbe(classifier.Object);

        var decision = await sut.ClassifyAsync(
            "refactor this class",
            new Dictionary<string, string>(),
            CancellationToken.None);

        decision.Label.Should().Be("CodeGeneration");
        decision.Confidence.Should().Be(0.77);
    }

    [Fact]
    public async Task ClassifyAsync_SynthesizesSingleTurnContextFromInput()
    {
        AgentTurnContext? captured = null;
        var classifier = new Mock<IRequestIntentClassifier>();
        classifier
            .Setup(c => c.ClassifyAsync(It.IsAny<AgentTurnContext>(), It.IsAny<CancellationToken>()))
            .Callback<AgentTurnContext, CancellationToken>((ctx, _) => captured = ctx)
            .ReturnsAsync(Assessment(RequestIntent.Research));

        var sut = new RequestIntentRouterProbe(classifier.Object);

        await sut.ClassifyAsync(
            "find every usage and summarize",
            new Dictionary<string, string>(),
            CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.UserMessage.Should().Be("find every usage and summarize");
        captured.TurnNumber.Should().Be(1);
    }

    private static Mock<IRequestIntentClassifier> ClassifierReturning(RequestIntent intent, double confidence)
    {
        var classifier = new Mock<IRequestIntentClassifier>();
        classifier
            .Setup(c => c.ClassifyAsync(It.IsAny<AgentTurnContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Assessment(intent, confidence));
        return classifier;
    }

    private static RequestIntentAssessment Assessment(RequestIntent intent, double confidence = 0.5) => new()
    {
        Intent = intent,
        Confidence = confidence,
        Source = ClassificationSource.LlmClassifier
    };
}
