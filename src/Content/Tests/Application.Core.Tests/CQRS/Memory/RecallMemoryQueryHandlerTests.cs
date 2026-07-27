using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.Core.CQRS.Memory;
using Domain.AI.KnowledgeGraph.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.Memory;

/// <summary>
/// Tests for <see cref="RecallMemoryQueryHandler"/> — recalled graph nodes must be projected to
/// the slim <see cref="MemoryEntry"/> wire shape, and node internals (owner, tenant, provenance,
/// extra properties) must never survive the projection.
/// </summary>
public sealed class RecallMemoryQueryHandlerTests
{
    private readonly Mock<IKnowledgeMemory> _memory = new();
    private readonly RecallMemoryQueryHandler _handler;

    public RecallMemoryQueryHandlerTests()
    {
        _handler = new RecallMemoryQueryHandler(
            _memory.Object, NullLogger<RecallMemoryQueryHandler>.Instance);
    }

    [Fact]
    public async Task Handle_MapsNodesToSlimEntries()
    {
        var createdAt = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
        _memory.Setup(m => m.RecallAsync("color", 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new GraphNode
                {
                    Id = "memory:t1:u1:favorite-color",
                    Name = "favorite-color",
                    Type = "Preference",
                    Properties = new Dictionary<string, string> { ["content"] = "blue" },
                    OwnerId = "u1",
                    TenantId = "t1",
                    CreatedAt = createdAt
                }
            ]);

        var result = await _handler.Handle(
            new RecallMemoryQuery { Query = "color", MaxResults = 5 }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var entry = result.Value!.Should().ContainSingle().Subject;
        entry.Key.Should().Be("favorite-color");
        entry.Content.Should().Be("blue");
        entry.EntityType.Should().Be("Preference");
        entry.CreatedAt.Should().Be(createdAt);
    }

    [Fact]
    public async Task Handle_NodeWithoutContentProperty_ProjectsEmptyContent()
    {
        // Recall can surface corpus entity nodes matched by graph traversal; they carry no
        // "content" property and must project as empty rather than throwing or leaking properties.
        _memory.Setup(m => m.RecallAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new GraphNode { Id = "azure:entity", Name = "azure", Type = "Technology" }
            ]);

        var result = await _handler.Handle(
            new RecallMemoryQuery { Query = "azure" }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Single().Content.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_PassesQueryAndMaxResultsThrough()
    {
        _memory.Setup(m => m.RecallAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var result = await _handler.Handle(
            new RecallMemoryQuery { Query = "deadline", MaxResults = 17 }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
        _memory.Verify(m => m.RecallAsync("deadline", 17, It.IsAny<CancellationToken>()), Times.Once);
    }
}
