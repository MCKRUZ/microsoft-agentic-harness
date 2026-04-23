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
	public required bool Success { get; init; }

	/// <summary>Error message if the ingestion failed; null on success.</summary>
	public string? Error { get; init; }
}
