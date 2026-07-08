using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.RAG.Retrieval;
using Infrastructure.AI.RAG.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.Retrieval;

public sealed class IterativeRetrieverTests
{
    private readonly Mock<IQueryDecomposer> _mockDecomposer = new();
    private readonly Mock<IHybridRetriever> _mockRetriever = new();
    private readonly Mock<ISufficiencyEvaluator> _mockSufficiency = new();

    private IterativeRetriever CreateIterativeRetriever(Action<AppConfig>? configure = null)
    {
        var config = RagTestData.CreateConfigMonitor(configure);
        return new IterativeRetriever(
            _mockDecomposer.Object,
            _mockRetriever.Object,
            _mockSufficiency.Object,
            config,
            Mock.Of<ILogger<IterativeRetriever>>());
    }

    [Fact]
    public async Task RetrieveIterativelyAsync_SimpleQuery_SingleHop()
    {
        var decomposed = new DecomposedQuery
        {
            OriginalQuery = "What is the default topK?",
            SubQueries = [new SubQuery { Text = "What is the default topK?", Order = 1 }],
        };
        _mockDecomposer
            .Setup(d => d.DecomposeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(decomposed);
        _mockRetriever
            .Setup(r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateRetrievalResults(3));
        _mockSufficiency
            .Setup(s => s.EvaluateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0.9);

        var retriever = CreateIterativeRetriever();
        var result = await retriever.RetrieveIterativelyAsync("What is the default topK?", topKPerHop: 5);

        result.Hops.Should().HaveCount(1);
        result.Hops[0].IsSufficient.Should().BeTrue();
        result.AggregatedResults.Should().HaveCount(3);
        result.BudgetExhausted.Should().BeFalse();
    }

    [Fact]
    public async Task RetrieveIterativelyAsync_ComplexQuery_MultipleHops()
    {
        var decomposed = new DecomposedQuery
        {
            OriginalQuery = "Complex query",
            SubQueries =
            [
                new SubQuery { Text = "Part A", Order = 1 },
                new SubQuery { Text = "Part B", Order = 2 },
            ],
        };
        _mockDecomposer
            .Setup(d => d.DecomposeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(decomposed);

        var resultsA = new List<RetrievalResult>
        {
            RagTestData.CreateRetrievalResult("chunk-a1", "Content A1", fusedScore: 0.9),
            RagTestData.CreateRetrievalResult("chunk-a2", "Content A2", fusedScore: 0.8),
        };
        var resultsB = new List<RetrievalResult>
        {
            RagTestData.CreateRetrievalResult("chunk-b1", "Content B1", fusedScore: 0.85),
        };

        var callCount = 0;
        _mockRetriever
            .Setup(r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount == 1 ? resultsA : resultsB;
            });
        _mockSufficiency
            .Setup(s => s.EvaluateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0.85);

        var retriever = CreateIterativeRetriever();
        var result = await retriever.RetrieveIterativelyAsync("Complex query", topKPerHop: 5);

        result.Hops.Should().HaveCount(2);
        result.AggregatedResults.Should().HaveCount(3);
    }

    [Fact]
    public async Task RetrieveIterativelyAsync_RespectsMaxHopsCap()
    {
        var subQueries = Enumerable.Range(1, 5)
            .Select(i => new SubQuery { Text = $"Part {i}", Order = i })
            .ToList();
        var decomposed = new DecomposedQuery { OriginalQuery = "Many parts", SubQueries = subQueries };
        _mockDecomposer
            .Setup(d => d.DecomposeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(decomposed);
        _mockRetriever
            .Setup(r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateRetrievalResults(2));
        _mockSufficiency
            .Setup(s => s.EvaluateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0.85);

        var retriever = CreateIterativeRetriever(c => c.AI.Rag.MultiHop.MaxHops = 3);
        var result = await retriever.RetrieveIterativelyAsync("Many parts", topKPerHop: 5);

        result.Hops.Should().HaveCountLessThanOrEqualTo(3);
    }

    [Fact]
    public async Task RetrieveIterativelyAsync_EnforceTokenBudget()
    {
        var decomposed = new DecomposedQuery
        {
            OriginalQuery = "Budget test",
            SubQueries =
            [
                new SubQuery { Text = "Part 1", Order = 1 },
                new SubQuery { Text = "Part 2", Order = 2 },
                new SubQuery { Text = "Part 3", Order = 3 },
            ],
        };
        _mockDecomposer
            .Setup(d => d.DecomposeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(decomposed);
        _mockRetriever
            .Setup(r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RetrievalResult>
            {
                RagTestData.CreateRetrievalResult("chunk-big", "This is a long content string that should consume a meaningful portion of the token budget for testing purposes.", fusedScore: 0.9),
            });
        _mockSufficiency
            .Setup(s => s.EvaluateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0.85);

        var retriever = CreateIterativeRetriever(c => c.AI.Rag.MultiHop.TokenBudgetPerHop = 10);
        var result = await retriever.RetrieveIterativelyAsync("Budget test", topKPerHop: 5);

        result.BudgetExhausted.Should().BeTrue();
    }

    [Fact]
    public async Task RetrieveIterativelyAsync_SubQueryDependencies_ExecutesInOrder()
    {
        var decomposed = new DecomposedQuery
        {
            OriginalQuery = "Dependent query",
            SubQueries =
            [
                new SubQuery { Text = "What is the architecture?", Order = 1 },
                new SubQuery { Text = "Based on the architecture, what needs changing?", Order = 2, DependsOnOrders = [1] },
            ],
        };
        _mockDecomposer
            .Setup(d => d.DecomposeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(decomposed);

        var executionOrder = new List<string>();
        _mockRetriever
            .Setup(r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns<string, int, string?, CancellationToken>((query, _, _, _) =>
            {
                executionOrder.Add(query);
                return Task.FromResult<IReadOnlyList<RetrievalResult>>(RagTestData.CreateRetrievalResults(2));
            });
        _mockSufficiency
            .Setup(s => s.EvaluateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0.85);

        var retriever = CreateIterativeRetriever();
        await retriever.RetrieveIterativelyAsync("Dependent query", topKPerHop: 5);

        executionOrder.Should().HaveCount(2);
        executionOrder[0].Should().Contain("architecture");
    }

    [Fact]
    public async Task RetrieveIterativelyAsync_SufficientFirstHop_StopsEarly()
    {
        var decomposed = new DecomposedQuery
        {
            OriginalQuery = "Simple",
            SubQueries = [new SubQuery { Text = "Simple question", Order = 1 }],
        };
        _mockDecomposer
            .Setup(d => d.DecomposeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(decomposed);
        _mockRetriever
            .Setup(r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateRetrievalResults(3));
        _mockSufficiency
            .Setup(s => s.EvaluateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0.95);

        var retriever = CreateIterativeRetriever();
        var result = await retriever.RetrieveIterativelyAsync("Simple", topKPerHop: 5);

        result.Hops.Should().HaveCount(1);
        result.Hops[0].IsSufficient.Should().BeTrue();
        _mockRetriever.Verify(
            r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RetrieveIterativelyAsync_AggregatesResultsAcrossHops()
    {
        var decomposed = new DecomposedQuery
        {
            OriginalQuery = "Multi-hop",
            SubQueries =
            [
                new SubQuery { Text = "Part 1", Order = 1 },
                new SubQuery { Text = "Part 2", Order = 2 },
            ],
        };
        _mockDecomposer
            .Setup(d => d.DecomposeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(decomposed);

        var callIndex = 0;
        _mockRetriever
            .Setup(r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callIndex++;
                return new List<RetrievalResult>
                {
                    RagTestData.CreateRetrievalResult($"chunk-hop{callIndex}-1", $"Content from hop {callIndex}", fusedScore: 0.9),
                    RagTestData.CreateRetrievalResult($"chunk-hop{callIndex}-2", $"More content from hop {callIndex}", fusedScore: 0.8),
                };
            });
        _mockSufficiency
            .Setup(s => s.EvaluateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0.85);

        var retriever = CreateIterativeRetriever();
        var result = await retriever.RetrieveIterativelyAsync("Multi-hop", topKPerHop: 5);

        result.AggregatedResults.Should().HaveCount(4);
    }

    [Fact]
    public async Task RetrieveIterativelyAsync_InsufficientHop_TriggersBoundedReRetrieval()
    {
        var decomposed = new DecomposedQuery
        {
            OriginalQuery = "Needs more detail",
            SubQueries = [new SubQuery { Text = "Detailed question", Order = 1 }],
        };
        _mockDecomposer
            .Setup(d => d.DecomposeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(decomposed);
        _mockRetriever
            .Setup(r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateRetrievalResults(3));
        // First verdict insufficient (0.2), re-retrieval verdict sufficient (0.9).
        _mockSufficiency
            .SetupSequence(s => s.EvaluateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0.2)
            .ReturnsAsync(0.9);

        var retriever = CreateIterativeRetriever(c => c.AI.Rag.MultiHop.MaxReRetriesPerHop = 1);
        var result = await retriever.RetrieveIterativelyAsync("Needs more detail", topKPerHop: 5);

        // Initial retrieval + one bounded re-retrieval for the insufficient hop.
        _mockRetriever.Verify(
            r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        result.Hops.Should().HaveCount(1);
        result.Hops[0].IsSufficient.Should().BeTrue();
        result.Hops[0].SufficiencyScore.Should().Be(0.9);
    }

    [Fact]
    public async Task RetrieveIterativelyAsync_InsufficientHop_ReRetryCapZero_DoesNotReRetrieve()
    {
        var decomposed = new DecomposedQuery
        {
            OriginalQuery = "Needs more detail",
            SubQueries = [new SubQuery { Text = "Detailed question", Order = 1 }],
        };
        _mockDecomposer
            .Setup(d => d.DecomposeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(decomposed);
        _mockRetriever
            .Setup(r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RagTestData.CreateRetrievalResults(3));
        _mockSufficiency
            .Setup(s => s.EvaluateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0.2);

        // Default cap (0) — insufficient hop advances immediately, legacy behavior preserved.
        var retriever = CreateIterativeRetriever();
        var result = await retriever.RetrieveIterativelyAsync("Needs more detail", topKPerHop: 5);

        _mockRetriever.Verify(
            r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        result.Hops[0].IsSufficient.Should().BeFalse();
    }

    [Fact]
    public async Task RetrieveIterativelyAsync_DeduplicatesChunksAcrossHops()
    {
        var decomposed = new DecomposedQuery
        {
            OriginalQuery = "Overlap query",
            SubQueries =
            [
                new SubQuery { Text = "Part 1", Order = 1 },
                new SubQuery { Text = "Part 2", Order = 2 },
            ],
        };
        _mockDecomposer
            .Setup(d => d.DecomposeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(decomposed);

        var sharedChunk = RagTestData.CreateRetrievalResult("chunk-shared", "Shared content", fusedScore: 0.9);
        var uniqueA = RagTestData.CreateRetrievalResult("chunk-a", "Unique A", fusedScore: 0.8);
        var uniqueB = RagTestData.CreateRetrievalResult("chunk-b", "Unique B", fusedScore: 0.7);

        var callCount = 0;
        _mockRetriever
            .Setup(r => r.RetrieveAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount == 1
                    ? new List<RetrievalResult> { sharedChunk, uniqueA }
                    : new List<RetrievalResult> { sharedChunk, uniqueB };
            });
        _mockSufficiency
            .Setup(s => s.EvaluateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RetrievalResult>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0.85);

        var retriever = CreateIterativeRetriever();
        var result = await retriever.RetrieveIterativelyAsync("Overlap query", topKPerHop: 5);

        result.AggregatedResults.Should().HaveCount(3);
        result.AggregatedResults.Select(r => r.Chunk.Id).Should().OnlyHaveUniqueItems();
    }
}
