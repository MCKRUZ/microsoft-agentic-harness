using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Interfaces.Compliance;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.Common.Compliance;

namespace Infrastructure.AI.KnowledgeGraph.Compliance;

/// <summary>
/// The knowledge graph's section of a personal-data export (#902): the person's nodes, including their remembered facts
/// whether trusted or quarantined, and the edges that belong to them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Reads what erasure deletes.</strong> Nodes stamped with the person's canonical id, found the way
/// <c>DefaultErasureOrchestrator</c> finds them. Edges are the ones stamped with the person's id, and the unowned ones
/// between two of the person's nodes. An edge stamped with another person's id is theirs even where it touches this
/// person's node, and is left out.
/// </para>
/// <para>
/// <strong>A record with no owner is never matched on its own.</strong> In the graph a missing owner means shared or
/// global. An unowned edge from one of the person's nodes to a shared one is usually document ingestion's: its source
/// chunk names the organisation's document and its properties carry that document's text, so it is left out even though
/// erasure removes it with the person's node. Erasure may remove more than the copy shows; the copy never shows what is
/// not the person's.
/// </para>
/// <para>
/// Edges are found by reading every edge and keeping the matching ones, as document removal does, because the graph
/// interface has no read of edges by owner. Export is rare and rate-limited, so the full read is accepted here.
/// </para>
/// </remarks>
public sealed class KnowledgeGraphExporter : IPersonalDataExporter
{
    private static readonly JsonSerializerOptions ExportJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IKnowledgeGraphStore _graph;

    /// <summary>Initializes a new instance of the <see cref="KnowledgeGraphExporter"/> class.</summary>
    /// <param name="graph">The knowledge graph store the host uses.</param>
    public KnowledgeGraphExporter(IKnowledgeGraphStore graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _graph = graph;
    }

    /// <inheritdoc />
    public string ExportStoreName => "knowledge graph";

    /// <inheritdoc />
    public async Task<IReadOnlyList<JsonElement>> ExportSubjectAsync(ExportSubject subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var owner = subject.CanonicalOwnerId;

        var nodes = (await _graph.GetNodesByOwnerAsync(owner, cancellationToken))
            .Where(n => string.Equals(n.OwnerId, owner, StringComparison.Ordinal))
            .ToList();
        var nodeIds = nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var edges = (await _graph.GetAllEdgesAsync(cancellationToken))
            .Where(e => string.Equals(e.OwnerId, owner, StringComparison.Ordinal)
                || (e.OwnerId is null && nodeIds.Contains(e.SourceNodeId) && nodeIds.Contains(e.TargetNodeId)));

        return
        [
            .. nodes.Select(n => Record("node", n)),
            .. edges.Select(e => Record("edge", e)),
        ];
    }

    private static JsonElement Record<T>(string kind, T value) =>
        JsonSerializer.SerializeToElement(new ExportedGraphRecord<T>(kind, value), ExportJson);

    /// <summary>One graph record in the export, labelled with whether it is a node or an edge.</summary>
    private sealed record ExportedGraphRecord<T>(string Kind, T Value);
}
