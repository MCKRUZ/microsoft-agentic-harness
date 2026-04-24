using FluentAssertions;
using Infrastructure.AI.RAG.Assembly;
using Infrastructure.AI.RAG.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.RAG.Tests.Assembly;

public sealed class PointerChunkExpanderTests
{
    private readonly PointerChunkExpander _expander = new(
        Mock.Of<ILogger<PointerChunkExpander>>());

    [Fact]
    public async Task ExpandAsync_ChunksWithoutParent_ReturnsUnchanged()
    {
        var chunks = new[]
        {
            RagTestData.CreateChunk("c1"),
            RagTestData.CreateChunk("c2")
        };

        var result = await _expander.ExpandAsync(chunks);

        result.Should().HaveCount(2);
        result.Select(c => c.Id).Should().ContainInOrder("c1", "c2");
    }

    [Fact]
    public async Task ExpandAsync_ChunksWithSiblings_IncludesSiblingsFromRetrievedSet()
    {
        var c1 = RagTestData.CreateChunk("c1", parentSectionId: "parent-1", siblingChunkIds: ["c2"]);
        var c2 = RagTestData.CreateChunk("c2", parentSectionId: "parent-1", siblingChunkIds: ["c1"]);
        var c3 = RagTestData.CreateChunk("c3");
        var chunks = new[] { c1, c3, c2 };

        var result = await _expander.ExpandAsync(chunks);

        result.Should().HaveCount(3);
        result.Select(c => c.Id).Should().Contain("c1").And.Contain("c2").And.Contain("c3");
    }

    [Fact]
    public async Task ExpandAsync_SiblingNotInRetrievedSet_NotIncluded()
    {
        var c1 = RagTestData.CreateChunk("c1", parentSectionId: "parent-1", siblingChunkIds: ["c-missing"]);
        var chunks = new[] { c1 };

        var result = await _expander.ExpandAsync(chunks);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("c1");
    }

    [Fact]
    public async Task ExpandAsync_DuplicateChunkIds_Deduplicates()
    {
        var c1 = RagTestData.CreateChunk("c1", parentSectionId: "parent-1", siblingChunkIds: ["c1"]);
        var chunks = new[] { c1, c1 };

        var result = await _expander.ExpandAsync(chunks);

        result.Should().HaveCount(1);
    }

    [Fact]
    public async Task ExpandAsync_EmptyInput_ReturnsEmpty()
    {
        var result = await _expander.ExpandAsync([]);

        result.Should().BeEmpty();
    }
}
