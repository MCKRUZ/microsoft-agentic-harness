namespace Application.Core.CQRS.RAG.IngestDocument;

/// <summary>Result of a document ingestion operation.</summary>
public record IngestDocumentResult
{
	/// <summary>Unique identifier for tracking this ingestion job.</summary>
	public required string JobId { get; init; }

	/// <summary>Number of chunks produced by the chunking pipeline.</summary>
	public required int ChunksProduced { get; init; }

	/// <summary>Total tokens sent to the embedding model.</summary>
	public required int TokensEmbedded { get; init; }

	/// <summary>Wall-clock duration of the entire ingestion pipeline.</summary>
	public required TimeSpan Duration { get; init; }

	/// <summary>Whether the ingestion completed without errors.</summary>
	/// <remarks>
	/// Refers to the core pipeline (parse → chunk → enrich → embed → vector/BM25 index).
	/// The optional graph stages report their own outcome via <see cref="GraphIndexed"/>
	/// and <see cref="KnowledgeGraphEnriched"/>: because the vector and BM25 writes have
	/// already committed when the graph stages run, a graph failure yields
	/// <c>Success == true</c> with the corresponding flag set to <c>false</c> — an honest
	/// partial success rather than a rollback of a usable ingest.
	/// </remarks>
	public required bool Success { get; init; }

	/// <summary>Error message if the ingestion failed; null on success.</summary>
	public string? Error { get; init; }

	/// <summary>
	/// Outcome of the optional corpus-graph indexing stage
	/// (<c>AppConfig:AI:Rag:GraphRag:IndexOnIngest</c>): <c>null</c> when the stage is
	/// disabled, <c>true</c> when the chunks were indexed into the GraphRag corpus graph,
	/// <c>false</c> when the stage ran but failed (details in server logs).
	/// </summary>
	public bool? GraphIndexed { get; init; }

	/// <summary>
	/// Outcome of the optional knowledge-graph enrichment stage
	/// (<c>AppConfig:AI:Rag:GraphRag:EnrichKnowledgeGraphOnIngest</c>): <c>null</c> when
	/// the stage is disabled, <c>true</c> when the <c>"kg-ingestion"</c> workflow stored
	/// extracted entities in the knowledge graph, <c>false</c> when the stage ran but
	/// failed (details in server logs).
	/// </summary>
	public bool? KnowledgeGraphEnriched { get; init; }
}
