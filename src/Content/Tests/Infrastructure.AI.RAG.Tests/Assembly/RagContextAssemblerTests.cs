using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;
using FluentAssertions;
using Infrastructure.AI.RAG.Assembly;
using Infrastructure.AI.RAG.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.Assembly;

public sealed class RagContextAssemblerTests
{
    private readonly Mock<IPointerExpander> _mockExpander = new();
    private readonly RagContextAssembler _assembler;

    public RagContextAssemblerTests()
    {
        _mockExpander
            .Setup(e => e.ExpandAsync(It.IsAny<IReadOnlyList<DocumentChunk>>(), It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<DocumentChunk>, CancellationToken>((chunks, _) =>
                Task.FromResult<IReadOnlyList<DocumentChunk>>(chunks.ToList()));

        _assembler = new RagContextAssembler(
            _mockExpander.Object,
            Mock.Of<ILogger<RagContextAssembler>>());
    }

    [Fact]
    public async Task AssembleAsync_SingleChunk_ProducesFormattedOutput()
    {
        var results = new[] { RagTestData.CreateRerankedResult("c1", "Hello world.") };

        var context = await _assembler.AssembleAsync(results, maxTokens: 4096);

        context.AssembledText.Should().Contain("Hello world.");
        context.AssembledText.Should().Contain("Source:");
        context.TotalTokens.Should().BeGreaterThan(0);
        context.WasTruncated.Should().BeFalse();
    }

    [Fact]
    public async Task AssembleAsync_MultipleChunks_SortedByRerankScore()
    {
        var results = new[]
        {
            RagTestData.CreateRerankedResult("low", "Low score chunk.", rerankScore: 0.3),
            RagTestData.CreateRerankedResult("high", "High score chunk.", rerankScore: 0.9),
            RagTestData.CreateRerankedResult("mid", "Mid score chunk.", rerankScore: 0.6)
        };

        var context = await _assembler.AssembleAsync(results, maxTokens: 4096);

        var highPos = context.AssembledText.IndexOf("High score chunk.", StringComparison.Ordinal);
        var midPos = context.AssembledText.IndexOf("Mid score chunk.", StringComparison.Ordinal);
        var lowPos = context.AssembledText.IndexOf("Low score chunk.", StringComparison.Ordinal);
        highPos.Should().BeLessThan(midPos);
        midPos.Should().BeLessThan(lowPos);
    }

    [Fact]
    public async Task AssembleAsync_ExceedsTokenBudget_TruncatesAndSetsFlag()
    {
        var content = new string('x', 100);
        var results = new[]
        {
            RagTestData.CreateRerankedResult("c1", content, rerankScore: 0.9),
            RagTestData.CreateRerankedResult("c2", content, rerankScore: 0.8),
            RagTestData.CreateRerankedResult("c3", content, rerankScore: 0.7)
        };

        // Budget of 60 tokens (240 chars) fits ~1 chunk with header but not all 3
        var context = await _assembler.AssembleAsync(results, maxTokens: 60);

        context.WasTruncated.Should().BeTrue();
        context.AssembledText.Should().NotBeEmpty();
        context.Citations.Count.Should().BeLessThan(3);
    }

    [Fact]
    public async Task AssembleAsync_AllFitInBudget_NotTruncated()
    {
        var results = RagTestData.CreateRerankedResults(3);

        var context = await _assembler.AssembleAsync(results, maxTokens: 4096);

        context.WasTruncated.Should().BeFalse();
        context.Citations.Should().HaveCount(3);
    }

    [Fact]
    public async Task AssembleAsync_TracksCitationsForIncludedChunks()
    {
        var results = new[]
        {
            RagTestData.CreateRerankedResult("c1", "First chunk content.", rerankScore: 0.9),
            RagTestData.CreateRerankedResult("c2", "Second chunk content.", rerankScore: 0.8)
        };

        var context = await _assembler.AssembleAsync(results, maxTokens: 4096);

        context.Citations.Should().HaveCount(2);
        context.Citations.Select(c => c.ChunkId).Should().Contain("c1").And.Contain("c2");
        context.Citations.Should().BeInAscendingOrder(c => c.StartOffset);
    }
}
