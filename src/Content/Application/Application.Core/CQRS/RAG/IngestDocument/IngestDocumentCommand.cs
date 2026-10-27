using Domain.AI.RAG.Enums;
using MediatR;

namespace Application.Core.CQRS.RAG.IngestDocument;

/// <summary>
/// Ingests a document into the RAG pipeline: parses, chunks, enriches,
/// embeds, and indexes the content for retrieval.
/// </summary>
/// <remarks>
/// Not an <see cref="Application.AI.Common.Interfaces.MediatR.IContentScreenable"/>, so neither pipeline screen runs on
/// it: content safety nor the prompt-injection pattern scan (#904). The command carries only the document's location.
/// Scoring a URI for harmful content means nothing, and sending it to an external screening service would hand over
/// whatever the URI carries: a SAS token in its query string, an internal path. The pattern scan of the URI covered a
/// far smaller surface than the document it names, whose content reaches the model through retrieval and which neither
/// screen ever read. A deployment that needs ingested content screened does it where the content is read.
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
