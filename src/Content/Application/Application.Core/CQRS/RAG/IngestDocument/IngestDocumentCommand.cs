using Application.AI.Common.Interfaces.MediatR;
using Domain.AI.Models;
using Domain.AI.RAG.Enums;
using MediatR;

namespace Application.Core.CQRS.RAG.IngestDocument;

/// <summary>
/// Ingests a document into the RAG pipeline: parses, chunks, enriches,
/// embeds, and indexes the content for retrieval.
/// </summary>
public record IngestDocumentCommand : IRequest<IngestDocumentResult>, IContentScreenable
{
	/// <inheritdoc />
	public string ContentToScreen => DocumentUri.ToString();

	/// <inheritdoc />
	public ContentScreeningTarget ScreeningTarget => ContentScreeningTarget.Input;

	/// <summary>URI of the document to ingest (file:// for local, https:// for remote).</summary>
	public required Uri DocumentUri { get; init; }

	/// <summary>Optional collection/index name. Uses default from config if null.</summary>
	public string? CollectionName { get; init; }

	/// <summary>Override the default chunking strategy for this document.</summary>
	public ChunkingStrategy? OverrideStrategy { get; init; }
}
