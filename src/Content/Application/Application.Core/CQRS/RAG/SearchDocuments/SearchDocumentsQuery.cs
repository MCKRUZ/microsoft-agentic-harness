using Application.AI.Common.Interfaces.MediatR;
using Domain.AI.Models;
using Domain.AI.RAG.Enums;
using MediatR;

namespace Application.Core.CQRS.RAG.SearchDocuments;

/// <summary>
/// Searches the RAG index using hybrid retrieval (dense + sparse) with optional reranking.
/// The query text is screened for content safety before execution and audited for
/// retrieval telemetry via the <see cref="IRetrievalAuditable"/> pipeline behavior.
/// </summary>
public record SearchDocumentsQuery : IRequest<SearchDocumentsResult>, IContentScreenable, IRetrievalAuditable
{
    /// <inheritdoc />
    public string ContentToScreen => Query;

    /// <inheritdoc />
    public ContentScreeningTarget ScreeningTarget => ContentScreeningTarget.Input;

    /// <inheritdoc />
    public string QueryText => Query;

    /// <summary>The natural-language search query text.</summary>
    public required string Query { get; init; }

    /// <summary>
    /// Override the number of final results to return. When <c>null</c>,
    /// uses <c>AppConfig:AI:Rag:Retrieval:RerankTopK</c>.
    /// </summary>
    public int? TopK { get; init; }

    /// <summary>
    /// Optional collection/index name. When <c>null</c>, uses the default
    /// collection from config.
    /// </summary>
    public string? CollectionName { get; init; }

    /// <summary>
    /// Override the default retrieval strategy for this search. When <c>null</c>,
    /// the handler uses hybrid vector + BM25 retrieval.
    /// </summary>
    public RetrievalStrategy? StrategyOverride { get; init; }
}
