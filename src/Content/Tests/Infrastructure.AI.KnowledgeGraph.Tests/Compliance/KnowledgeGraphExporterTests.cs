using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Compliance;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.Compliance;
using Infrastructure.AI.KnowledgeGraph.InMemory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Compliance;

/// <summary>
/// The knowledge graph's export section (#902): the person's nodes and the edges that go with them, never a record that
/// is shared, global or someone else's.
/// </summary>
public sealed class KnowledgeGraphExporterTests
{
    private const string Owner = "Owner-1";
    private readonly InMemoryGraphStore _graph = new(NullLogger<InMemoryGraphStore>.Instance);

    private async Task<List<(string Kind, string Id)>> ExportAsync(string owner = Owner)
    {
        var records = await new KnowledgeGraphExporter(_graph)
            .ExportSubjectAsync(new ExportSubject(owner, []), CancellationToken.None);
        return [.. records.Select(r => (r.GetProperty("kind").GetString()!, r.GetProperty("value").GetProperty("id").GetString()!))];
    }

    private static GraphNode Node(string id, string? owner) => new() { Id = id, Name = id, Type = "Memory", OwnerId = owner };

    private static GraphEdge Edge(string id, string source, string target, string? owner) =>
        new() { Id = id, SourceNodeId = source, TargetNodeId = target, Predicate = "p", ChunkId = id, OwnerId = owner };

    [Fact]
    public async Task ThePersonsNodes_AreExported_WhateverCaseTheIdWasIssuedIn()
    {
        await _graph.AddNodesAsync([Node("mine", "owner-1"), Node("theirs", "owner-2"), Node("global", null)]);

        (await ExportAsync("OWNER-1")).Should().Equal(("node", "mine"));
    }

    [Fact]
    public async Task EdgesTheyOwn_AndUnownedEdgesBetweenTheirNodes_AreExported_OthersAreNot()
    {
        await _graph.AddNodesAsync([Node("mine", "owner-1"), Node("mine-too", "owner-1"), Node("theirs", "owner-2"), Node("global", null)]);
        await _graph.AddEdgesAsync(
        [
            Edge("owned", "global", "theirs", "owner-1"),
            Edge("unowned-between-mine", "mine", "mine-too", null),
            Edge("unowned-in", "global", "mine", null),
            Edge("unowned-out", "mine", "global", null),
            Edge("someone-elses-into-mine", "theirs", "mine", "owner-2"),
            Edge("unowned-elsewhere", "global", "theirs", null),
        ]);

        // An unowned edge into shared knowledge is document ingestion's: its chunk and text are the organisation's.
        (await ExportAsync()).Where(r => r.Kind == "edge").Select(r => r.Id)
            .Should().BeEquivalentTo(["owned", "unowned-between-mine"]);
    }

    [Fact]
    public async Task APersonWithNothing_GetsAnEmptySection_NotTheSharedRecords()
    {
        await _graph.AddNodesAsync([Node("global", null)]);
        await _graph.AddEdgesAsync([Edge("shared", "global", "global", null)]);

        (await ExportAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ANodesContent_IsInTheRecord()
    {
        await _graph.AddNodesAsync([Node("mine", "owner-1") with { Properties = new Dictionary<string, string> { ["fact"] = "likes tea" } }]);

        var record = (await new KnowledgeGraphExporter(_graph).ExportSubjectAsync(new ExportSubject(Owner, []), CancellationToken.None)).Single();

        record.GetProperty("value").GetProperty("properties").GetProperty("fact").GetString().Should().Be("likes tea");
    }
}
