using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;
using FluentAssertions;
using Infrastructure.AI.RAG.Evaluation;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.Evaluation;

public sealed class QueryTypeRouterProbeTests
{
    [Fact]
    public void Key_IsQueryType()
    {
        var sut = new QueryTypeRouterProbe(Mock.Of<IQueryClassifier>());
        sut.Key.Should().Be("query_type");
    }

    [Fact]
    public async Task ClassifyAsync_MapsQueryTypeToLabel_AndStrategyToSecondaryLabel()
    {
        var classifier = new Mock<IQueryClassifier>();
        classifier
            .Setup(c => c.ClassifyAsync("how do A and B interact?", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryClassification
            {
                Type = QueryType.MultiHop,
                Strategy = RetrievalStrategy.GraphRag,
                Confidence = 0.84,
                Reasoning = "spans two sections"
            });

        var sut = new QueryTypeRouterProbe(classifier.Object);

        var decision = await sut.ClassifyAsync(
            "how do A and B interact?",
            new Dictionary<string, string>(),
            CancellationToken.None);

        decision.Label.Should().Be("MultiHop");
        decision.SecondaryLabel.Should().Be("GraphRag");
        decision.Confidence.Should().Be(0.84);
        decision.Reasoning.Should().Be("spans two sections");
    }

    [Fact]
    public async Task ClassifyAsync_PassesQueryThroughVerbatim()
    {
        var classifier = new Mock<IQueryClassifier>();
        classifier
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryClassification
            {
                Type = QueryType.SimpleLookup,
                Strategy = RetrievalStrategy.HybridVectorBm25,
                Confidence = 0.95
            });

        var sut = new QueryTypeRouterProbe(classifier.Object);

        await sut.ClassifyAsync("what is the timeout?", new Dictionary<string, string>(), CancellationToken.None);

        classifier.Verify(c => c.ClassifyAsync("what is the timeout?", It.IsAny<CancellationToken>()), Times.Once);
    }
}
