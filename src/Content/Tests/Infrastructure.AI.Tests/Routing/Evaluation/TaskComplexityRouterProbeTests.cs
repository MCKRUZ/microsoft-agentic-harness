using Application.AI.Common.Interfaces.Routing;
using Domain.AI.Routing.Enums;
using Domain.AI.Routing.Models;
using FluentAssertions;
using Infrastructure.AI.Routing.Evaluation;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Routing.Evaluation;

public sealed class TaskComplexityRouterProbeTests
{
    [Fact]
    public void Key_IsTaskComplexity()
    {
        var sut = new TaskComplexityRouterProbe(Mock.Of<ITaskComplexityClassifier>());
        sut.Key.Should().Be("task_complexity");
    }

    [Fact]
    public async Task ClassifyAsync_MapsComplexityToLabel()
    {
        var classifier = ClassifierReturning(TaskComplexity.Complex, confidence: 0.77);
        var sut = new TaskComplexityRouterProbe(classifier.Object);

        var decision = await sut.ClassifyAsync(
            "design and implement a new subsystem",
            new Dictionary<string, string>(),
            CancellationToken.None);

        decision.Label.Should().Be("Complex");
        decision.Confidence.Should().Be(0.77);
    }

    [Fact]
    public async Task ClassifyAsync_SynthesizesSingleTurnContextFromInput()
    {
        AgentTurnContext? captured = null;
        var classifier = new Mock<ITaskComplexityClassifier>();
        classifier
            .Setup(c => c.ClassifyAsync(It.IsAny<AgentTurnContext>(), It.IsAny<CancellationToken>()))
            .Callback<AgentTurnContext, CancellationToken>((ctx, _) => captured = ctx)
            .ReturnsAsync(Assessment(TaskComplexity.Moderate));

        var sut = new TaskComplexityRouterProbe(classifier.Object);

        await sut.ClassifyAsync(
            "find every usage and summarize",
            new Dictionary<string, string> { ["tool_count"] = "8" },
            CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.UserMessage.Should().Be("find every usage and summarize");
        captured.TurnNumber.Should().Be(1);
        captured.AvailableToolCount.Should().Be(8);
    }

    [Fact]
    public async Task ClassifyAsync_MissingOrBadToolCount_DefaultsToZero()
    {
        AgentTurnContext? captured = null;
        var classifier = new Mock<ITaskComplexityClassifier>();
        classifier
            .Setup(c => c.ClassifyAsync(It.IsAny<AgentTurnContext>(), It.IsAny<CancellationToken>()))
            .Callback<AgentTurnContext, CancellationToken>((ctx, _) => captured = ctx)
            .ReturnsAsync(Assessment(TaskComplexity.Trivial));

        var sut = new TaskComplexityRouterProbe(classifier.Object);

        await sut.ClassifyAsync(
            "hi",
            new Dictionary<string, string> { ["tool_count"] = "not-a-number" },
            CancellationToken.None);

        captured!.AvailableToolCount.Should().Be(0);
    }

    private static Mock<ITaskComplexityClassifier> ClassifierReturning(TaskComplexity complexity, double confidence)
    {
        var classifier = new Mock<ITaskComplexityClassifier>();
        classifier
            .Setup(c => c.ClassifyAsync(It.IsAny<AgentTurnContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Assessment(complexity, confidence));
        return classifier;
    }

    private static TaskComplexityAssessment Assessment(TaskComplexity complexity, double confidence = 0.5) => new()
    {
        Complexity = complexity,
        Confidence = confidence,
        Source = ClassificationSource.LlmClassifier
    };
}
