using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.RAG;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.Provenance;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Provenance;

/// <summary>
/// Tests for <see cref="DefaultProvenanceStamper"/> — stamp creation,
/// node/edge stamping, and config-driven enable/disable behavior.
/// </summary>
public sealed class DefaultProvenanceStamperTests
{
    private readonly Mock<IOptionsMonitor<AppConfig>> _configMonitor;
    private readonly FakeTimeProvider _timeProvider;

    public DefaultProvenanceStamperTests()
    {
        _configMonitor = new Mock<IOptionsMonitor<AppConfig>>();
        _timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 4, 23, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void CreateStamp_PopulatesAllFields()
    {
        var stamper = CreateStamper(provenanceEnabled: true);

        var stamp = stamper.CreateStamp(
            "rag_ingestion", "entity_extraction",
            sourceDocumentId: "doc-1",
            extractionConfidence: 0.95,
            modifiedBy: "user-42");

        stamp.SourcePipeline.Should().Be("rag_ingestion");
        stamp.SourceTask.Should().Be("entity_extraction");
        stamp.Timestamp.Should().Be(_timeProvider.GetUtcNow());
        stamp.SourceDocumentId.Should().Be("doc-1");
        stamp.ExtractionConfidence.Should().Be(0.95);
        stamp.LastModifiedBy.Should().Be("user-42");
    }

    [Fact]
    public void CreateStamp_OptionalFieldsDefaultToNull()
    {
        var stamper = CreateStamper(provenanceEnabled: true);

        var stamp = stamper.CreateStamp("pipeline", "task");

        stamp.SourceDocumentId.Should().BeNull();
        stamp.ExtractionConfidence.Should().BeNull();
        stamp.LastModifiedBy.Should().BeNull();
    }

    [Fact]
    public void CreateStamp_UsesTimeProviderTimestamp()
    {
        var specificTime = new DateTimeOffset(2025, 1, 15, 8, 30, 0, TimeSpan.Zero);
        var tp = new FakeTimeProvider(specificTime);
        var stamper = new DefaultProvenanceStamper(_configMonitor.Object, tp);
        SetConfig(true);

        var stamp = stamper.CreateStamp("p", "t");

        stamp.Timestamp.Should().Be(specificTime);
    }

    [Fact]
    public void StampNode_ProvenanceEnabled_AttachesStamp()
    {
        var stamper = CreateStamper(provenanceEnabled: true);
        var node = CreateNode();
        var stamp = stamper.CreateStamp("pipeline", "task");

        var stamped = stamper.StampNode(node, stamp);

        stamped.Provenance.Should().NotBeNull();
        stamped.Provenance!.SourcePipeline.Should().Be("pipeline");
        stamped.Id.Should().Be(node.Id);
        stamped.Name.Should().Be(node.Name);
    }

    [Fact]
    public void StampNode_ProvenanceDisabled_ReturnsNodeUnchanged()
    {
        var stamper = CreateStamper(provenanceEnabled: false);
        var node = CreateNode();
        var stamp = new ProvenanceStamp
        {
            SourcePipeline = "p", SourceTask = "t",
            Timestamp = DateTimeOffset.UtcNow
        };

        var result = stamper.StampNode(node, stamp);

        result.Provenance.Should().BeNull();
        result.Should().BeSameAs(node);
    }

    [Fact]
    public void StampEdge_ProvenanceEnabled_AttachesStamp()
    {
        var stamper = CreateStamper(provenanceEnabled: true);
        var edge = CreateEdge();
        var stamp = stamper.CreateStamp("pipeline", "relationship_detection");

        var stamped = stamper.StampEdge(edge, stamp);

        stamped.Provenance.Should().NotBeNull();
        stamped.Provenance!.SourceTask.Should().Be("relationship_detection");
        stamped.SourceNodeId.Should().Be(edge.SourceNodeId);
    }

    [Fact]
    public void StampEdge_ProvenanceDisabled_ReturnsEdgeUnchanged()
    {
        var stamper = CreateStamper(provenanceEnabled: false);
        var edge = CreateEdge();
        var stamp = new ProvenanceStamp
        {
            SourcePipeline = "p", SourceTask = "t",
            Timestamp = DateTimeOffset.UtcNow
        };

        var result = stamper.StampEdge(edge, stamp);

        result.Provenance.Should().BeNull();
        result.Should().BeSameAs(edge);
    }

    [Fact]
    public void StampNode_PreservesExistingNodeData()
    {
        var stamper = CreateStamper(provenanceEnabled: true);
        var node = new GraphNode
        {
            Id = "n1", Name = "Azure", Type = "Technology",
            ChunkIds = ["c1", "c2"],
            Properties = new Dictionary<string, string> { ["region"] = "eastus" }
        };
        var stamp = stamper.CreateStamp("p", "t");

        var stamped = stamper.StampNode(node, stamp);

        stamped.ChunkIds.Should().BeEquivalentTo(["c1", "c2"]);
        stamped.Properties.Should().ContainKey("region");
        stamped.Type.Should().Be("Technology");
    }

    [Fact]
    public void StampNode_ReplacesExistingProvenance()
    {
        var stamper = CreateStamper(provenanceEnabled: true);
        var oldStamp = new ProvenanceStamp
        {
            SourcePipeline = "old", SourceTask = "old_task",
            Timestamp = DateTimeOffset.UtcNow.AddDays(-1)
        };
        var node = CreateNode() with { Provenance = oldStamp };
        var newStamp = stamper.CreateStamp("new_pipeline", "new_task");

        var stamped = stamper.StampNode(node, newStamp);

        stamped.Provenance!.SourcePipeline.Should().Be("new_pipeline");
    }

    private DefaultProvenanceStamper CreateStamper(bool provenanceEnabled)
    {
        SetConfig(provenanceEnabled);
        return new DefaultProvenanceStamper(_configMonitor.Object, _timeProvider);
    }

    private void SetConfig(bool provenanceEnabled)
    {
        _configMonitor.Setup(m => m.CurrentValue).Returns(new AppConfig
        {
            AI = new AIConfig
            {
                Rag = new RagConfig
                {
                    GraphRag = new GraphRagConfig { ProvenanceEnabled = provenanceEnabled }
                }
            }
        });
    }

    private static GraphNode CreateNode() =>
        new() { Id = "n1", Name = "Test", Type = "Entity" };

    private static GraphEdge CreateEdge() =>
        new()
        {
            Id = "e1", SourceNodeId = "n1", TargetNodeId = "n2",
            Predicate = "uses", ChunkId = "c1"
        };

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;
        public FakeTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
