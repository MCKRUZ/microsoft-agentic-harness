using Application.Core.CQRS.RAG.IngestDocument;
using Application.Core.CQRS.RAG.SearchDocuments;
using Domain.AI.RAG.Enums;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Presentation.AgentHub.Controllers;

/// <summary>
/// REST API for document ingestion and search against the RAG pipeline.
/// Delegates all work to MediatR command/query handlers, which execute the
/// full ingestion (parse, chunk, enrich, embed, index) and retrieval
/// (hybrid search, rerank, CRAG evaluate, assemble) pipelines respectively.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class DocumentsController : ControllerBase
{
    private readonly IMediator _mediator;

    /// <summary>Initializes the controller with its MediatR dependency.</summary>
    public DocumentsController(IMediator mediator)
    {
        _mediator = mediator;
    }

    /// <summary>
    /// Ingests a document into the RAG index. The document is parsed, chunked,
    /// contextually enriched, embedded, and indexed in both the vector store
    /// and BM25 keyword index.
    /// </summary>
    /// <param name="request">The ingestion request with document URI and optional settings.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Ingestion result with chunk count, token count, and job ID.</returns>
    /// <response code="200">Document ingested successfully.</response>
    /// <response code="400">Invalid request (missing URI or bad format).</response>
    [HttpPost("ingest")]
    [ProducesResponseType(typeof(IngestDocumentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IngestDocumentResult>> Ingest(
        [FromBody] IngestRequest request,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(request.Uri, UriKind.Absolute, out var uri))
            return BadRequest(new { error = "Invalid URI format." });

        var command = new IngestDocumentCommand
        {
            DocumentUri = uri,
            CollectionName = request.Collection
        };

        var result = await _mediator.Send(command, cancellationToken);

        if (!result.Success)
            return BadRequest(new { error = result.Error });

        return Ok(result);
    }

    /// <summary>
    /// Searches the RAG index using hybrid retrieval with optional reranking
    /// and strategy override. Returns reranked results with timing metadata.
    /// </summary>
    /// <param name="request">The search request with query text and optional parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Search results with reranked chunks and pipeline metadata.</returns>
    /// <response code="200">Search completed (may contain zero results).</response>
    /// <response code="400">Invalid request (missing query).</response>
    [HttpPost("search")]
    [ProducesResponseType(typeof(SearchDocumentsResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SearchDocumentsResult>> Search(
        [FromBody] SearchRequest request,
        CancellationToken cancellationToken)
    {
        var query = new SearchDocumentsQuery
        {
            Query = request.Query,
            TopK = request.TopK,
            CollectionName = request.Collection,
            StrategyOverride = request.Strategy
        };

        var result = await _mediator.Send(query, cancellationToken);

        if (!result.Success)
            return BadRequest(new { error = result.Error });

        return Ok(result);
    }
}

/// <summary>Request body for <c>POST /api/documents/ingest</c>.</summary>
/// <param name="Uri">Absolute URI of the document to ingest (file:// or https://).</param>
/// <param name="Collection">Optional collection/index name; null uses the default.</param>
public sealed record IngestRequest(string Uri, string? Collection = null);

/// <summary>Request body for <c>POST /api/documents/search</c>.</summary>
/// <param name="Query">Natural-language search query.</param>
/// <param name="TopK">Maximum number of results to return; null uses config default.</param>
/// <param name="Collection">Optional collection/index name; null uses the default.</param>
/// <param name="Strategy">Override the default retrieval strategy; null uses classification.</param>
public sealed record SearchRequest(
    string Query,
    int? TopK = null,
    string? Collection = null,
    RetrievalStrategy? Strategy = null);
