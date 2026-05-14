using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.RAG.Models;

namespace Application.Core.Workflows.KnowledgeGraph;

/// <summary>
/// Input to the knowledge graph ingestion workflow. Wraps a batch of document chunks
/// that should be processed through entity extraction, provenance stamping, and graph storage.
/// </summary>
/// <param name="Chunks">The document chunks to extract entities and relationships from.</param>
/// <param name="SourcePipeline">
/// The pipeline name for provenance stamping (e.g., "rag_ingestion", "manual_import").
/// </param>
public sealed record KgIngestionInput(
    IReadOnlyList<DocumentChunk> Chunks,
    string SourcePipeline = "rag_ingestion");

/// <summary>
/// Output of the entity extraction stage. Contains the raw nodes and edges extracted
/// from document chunks by the LLM, before provenance stamping.
/// </summary>
/// <param name="Nodes">Extracted entity nodes with chunk references.</param>
/// <param name="Edges">Extracted relationship edges between entity nodes.</param>
/// <param name="ChunksProcessed">Number of source chunks that were processed.</param>
/// <param name="SourcePipeline">
/// The pipeline name from <see cref="KgIngestionInput.SourcePipeline"/>,
/// threaded through for provenance stamping.
/// </param>
public sealed record ExtractedEntities(
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges,
    int ChunksProcessed,
    string SourcePipeline);

/// <summary>
/// Output of the provenance stamping stage. Contains nodes and edges with
/// <see cref="ProvenanceStamp"/> metadata attached, ready for graph storage.
/// </summary>
/// <param name="Nodes">Provenance-stamped entity nodes.</param>
/// <param name="Edges">Provenance-stamped relationship edges.</param>
/// <param name="ChunksProcessed">Number of source chunks processed, carried through for the final result.</param>
/// <param name="SourcePipeline">The pipeline name used for stamping, preserved for audit logging.</param>
public sealed record StampedEntities(
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges,
    int ChunksProcessed,
    string SourcePipeline);

/// <summary>
/// Final output of the knowledge graph ingestion workflow. Reports the counts of nodes
/// and edges successfully stored in the graph backend.
/// </summary>
/// <param name="NodesStored">Number of nodes added or merged in the graph store.</param>
/// <param name="EdgesStored">Number of edges added in the graph store.</param>
/// <param name="ChunksProcessed">Number of source chunks that were processed.</param>
public sealed record KgIngestionResult(
    int NodesStored,
    int EdgesStored,
    int ChunksProcessed);
