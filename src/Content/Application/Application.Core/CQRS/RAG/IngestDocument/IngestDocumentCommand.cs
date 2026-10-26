using Domain.AI.RAG.Enums;
using MediatR;

namespace Application.Core.CQRS.RAG.IngestDocument;

/// <summary>
/// Ingests a document into the RAG pipeline: parses, chunks, enriches,
/// embeds, and indexes the content for retrieval.
/// </summary>
/// <remarks>
/// Not content-screened. The command carries only the document's location, and scoring a URI for harmful content means
/// nothing, while sending it to an external screening service would hand over whatever the URI carries: a SAS token in
/// its query string, an internal path (#904). The document's content is not screened either; a deployment that needs
/// ingested content screened does it where the content is read.
/// </remarks>
public record IngestDocumentCommand : IRequest<IngestDocumentResult>
{
	/// <summary>URI of the document to ingest (file:// for local, https:// for remote).</summary>
	public required Uri DocumentUri { get; init; }

	/// <summary>Optional collection/index name. Uses default from config if null.</summary>
	public string? CollectionName { get; init; }

	/// <summary>Override the default chunking strategy for this document.</summary>
	public ChunkingStrategy? OverrideStrategy { get; init; }
}
