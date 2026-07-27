using System.Diagnostics;
using Application.AI.Common.Interfaces.RAG;
using Application.AI.Common.OpenTelemetry.Metrics;
using Application.Core.Workflows.KnowledgeGraph;
using Domain.AI.RAG.Models;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config;
using MediatR;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.Core.CQRS.RAG.IngestDocument;

/// <summary>
/// Orchestrates the document ingestion pipeline: parse, extract structure,
/// chunk, enrich, embed, and index — plus two opt-in graph-build stages
/// (corpus-graph indexing and knowledge-graph enrichment) that run after the
/// vector and BM25 indexes commit.
/// </summary>
public sealed class IngestDocumentCommandHandler
	: IRequestHandler<IngestDocumentCommand, IngestDocumentResult>
{
	private static readonly ActivitySource ActivitySource = new("AgenticHarness.RAG.Ingestion");

	private readonly IDocumentParser _parser;
	private readonly IStructureExtractor _structureExtractor;
	private readonly IChunkingService _chunker;
	private readonly IContextualEnricher _enricher;
	private readonly IRaptorSummarizer _raptorSummarizer;
	private readonly IEmbeddingService _embeddingService;
	private readonly IVectorStore _vectorStore;
	private readonly IBm25Store _bm25Store;
	private readonly ILogger<IngestDocumentCommandHandler> _logger;
	private readonly IOptionsMonitor<AppConfig> _appConfigMonitor;
	private readonly IServiceProvider _serviceProvider;

	public IngestDocumentCommandHandler(
		IDocumentParser parser,
		IStructureExtractor structureExtractor,
		IChunkingService chunker,
		IContextualEnricher enricher,
		IRaptorSummarizer raptorSummarizer,
		IEmbeddingService embeddingService,
		IVectorStore vectorStore,
		IBm25Store bm25Store,
		ILogger<IngestDocumentCommandHandler> logger,
		IOptionsMonitor<AppConfig> appConfigMonitor,
		IServiceProvider serviceProvider)
	{
		_parser = parser;
		_structureExtractor = structureExtractor;
		_chunker = chunker;
		_enricher = enricher;
		_raptorSummarizer = raptorSummarizer;
		_embeddingService = embeddingService;
		_vectorStore = vectorStore;
		_bm25Store = bm25Store;
		_logger = logger;
		_appConfigMonitor = appConfigMonitor;
		// The graph-build stages resolve their collaborators lazily from the provider:
		// IGraphRagService is registered only when the graph database backend is enabled,
		// and the "kg-ingestion" keyed workflow should not be built on every ingest when
		// enrichment is off. Constructor-injecting either would break hosts that run with
		// the (default-off) graph stages disabled.
		_serviceProvider = serviceProvider;
	}

	public async Task<IngestDocumentResult> Handle(
		IngestDocumentCommand request,
		CancellationToken cancellationToken)
	{
		using var activity = ActivitySource.StartActivity("rag.ingest.pipeline");
		activity?.SetTag(RagConventions.ModelOperation, "ingest");
		var sw = Stopwatch.StartNew();
		var jobId = Guid.NewGuid().ToString("N")[..12];

		// Captured once chunks exist so the catch path can compensate partial store writes.
		string? documentId = null;

		try
		{
			_logger.LogInformation(
				"RAG ingestion started: {DocumentUri}, Job: {JobId}",
				request.DocumentUri, jobId);

			// 1. Parse document to markdown
			var markdown = await _parser.ParseAsync(request.DocumentUri, cancellationToken);

			// 2. Extract structure (skeleton tree)
			var structure = _structureExtractor.ExtractStructure(markdown);
			activity?.SetTag("rag.ingest.skeleton_depth", structure.Children.Count);

			// 3. Chunk the document
			var chunks = await _chunker.ChunkAsync(
				markdown, structure, request.DocumentUri, cancellationToken);
			activity?.SetTag(RagConventions.IngestChunksProduced, chunks.Count);

			// All chunks (including RAPTOR summaries) share the source DocumentId, which is
			// what both stores key deletion by. Capturing it here makes document-scoped
			// compensation possible if a later parallel store write partially fails.
			documentId = chunks.Count > 0 ? chunks[0].DocumentId : null;

			// 4. Contextual enrichment (if enabled)
			var ragConfig = _appConfigMonitor.CurrentValue.AI.Rag;
			if (ragConfig.Ingestion.EnableContextualEnrichment)
			{
				chunks = await _enricher.EnrichAsync(chunks, markdown, cancellationToken);
			}

			// 5. RAPTOR summaries (if enabled) — returns originals + summaries
			if (ragConfig.Ingestion.EnableRaptorSummaries)
			{
				chunks = await _raptorSummarizer.SummarizeAsync(
					chunks, ragConfig.Ingestion.MaxRaptorDepth, cancellationToken);
			}

			// 6. Generate embeddings
			chunks = await _embeddingService.EmbedAsync(chunks, cancellationToken);
			var totalTokens = chunks.Sum(c => c.Tokens);

			// 7. Index in vector store and BM25 store in parallel
			await Task.WhenAll(
				_vectorStore.IndexAsync(chunks, request.CollectionName, cancellationToken),
				_bm25Store.IndexAsync(chunks, request.CollectionName, cancellationToken));

			// 8. Opt-in graph-build stages. These run AFTER the vector/BM25 commit and are
			// deliberately non-fatal: the searchable ingest already succeeded, so a graph
			// failure (including cancellation mid-stage) is logged and surfaced honestly on
			// the result flags instead of failing the command — which would trigger the
			// catch-path compensation and delete a perfectly usable ingest.
			// The two stages write physically separate stores and only read the shared
			// chunk list, so they run concurrently; neither ever throws, so awaiting them
			// in sequence after starting both needs no aggregate exception handling.
			var indexTask = ragConfig.GraphRag.IndexOnIngest
				? TryIndexCorpusGraphAsync(chunks, jobId, cancellationToken)
				: null;
			var enrichTask = ragConfig.GraphRag.EnrichKnowledgeGraphOnIngest
				? TryEnrichKnowledgeGraphAsync(chunks, jobId, cancellationToken)
				: null;
			bool? graphIndexed = indexTask is null ? null : await indexTask;
			bool? knowledgeGraphEnriched = enrichTask is null ? null : await enrichTask;

			sw.Stop();

			RagIngestionMetrics.Documents.Add(1);
			RagIngestionMetrics.ChunksProduced.Add(chunks.Count);
			RagIngestionMetrics.TokensEmbedded.Add(totalTokens);
			RagIngestionMetrics.Duration.Record(sw.Elapsed.TotalMilliseconds);

			_logger.LogInformation(
				"RAG ingestion completed: {JobId}, {ChunkCount} chunks, {TokenCount} tokens, {Duration}ms",
				jobId, chunks.Count, totalTokens, sw.ElapsedMilliseconds);

			return new IngestDocumentResult
			{
				JobId = jobId,
				ChunksProduced = chunks.Count,
				TokensEmbedded = totalTokens,
				Duration = sw.Elapsed,
				Success = true,
				GraphIndexed = graphIndexed,
				KnowledgeGraphEnriched = knowledgeGraphEnriched
			};
		}
		catch (Exception ex)
		{
			sw.Stop();
			_logger.LogError(ex, "RAG ingestion failed: {JobId}", jobId);
			RagIngestionMetrics.Errors.Add(1);

			// Compensate partial writes: the vector and BM25 stores are written in parallel,
			// so a single-store failure can leave the other store's derived copies (chunks,
			// RAPTOR summaries) persisted with no clean way to erase them later. Both deletes
			// are document-scoped and idempotent, so calling them is safe even for the store
			// that wrote nothing.
			if (documentId is not null)
			{
				await CompensatePartialIngestAsync(documentId, request.CollectionName, jobId);
			}

			return new IngestDocumentResult
			{
				JobId = jobId,
				ChunksProduced = 0,
				TokensEmbedded = 0,
				Duration = sw.Elapsed,
				Success = false,
				Error = "Document ingestion failed. Check server logs for details."
			};
		}
	}

	/// <summary>
	/// Best-effort rollback of already-written derived copies for a failed ingest.
	/// Deletes the document's entries from both the vector and BM25 stores so a partial
	/// write does not leave un-erasable orphans. A compensating delete that itself fails
	/// is logged and swallowed: it must not mask the original ingestion failure or abort
	/// the sibling store's cleanup.
	/// </summary>
	private async Task CompensatePartialIngestAsync(
		string documentId, string? collectionName, string jobId)
	{
		// Use CancellationToken.None: cleanup must complete even when the original
		// ingestion failed due to cancellation — abandoning it would defeat the purpose.
		await TryDeleteAsync(
			ct => _vectorStore.DeleteAsync(documentId, collectionName, ct),
			documentId, jobId, "vector");
		await TryDeleteAsync(
			ct => _bm25Store.DeleteAsync(documentId, collectionName, ct),
			documentId, jobId, "BM25");
	}

	private async Task TryDeleteAsync(
		Func<CancellationToken, Task> delete, string documentId, string jobId, string storeName)
	{
		try
		{
			await delete(CancellationToken.None);
		}
		catch (Exception ex)
		{
			_logger.LogError(
				ex,
				"RAG ingestion compensation failed to delete {DocumentId} from {StoreName} store: {JobId}",
				documentId, storeName, jobId);
		}
	}

	/// <summary>
	/// Best-effort corpus-graph indexing stage (<c>GraphRagConfig.IndexOnIngest</c>).
	/// Feeds the ingested chunks to <see cref="IGraphRagService.IndexCorpusAsync"/> so the
	/// GraphRag retrieval strategy queries a populated graph. Resolves the service lazily
	/// because it is registered only when <c>AppConfig:AI:Rag:GraphDatabase:Enabled</c> is
	/// <c>true</c>. Never throws — including on cancellation — because the vector and BM25
	/// writes have already committed; the outcome is reported on
	/// <see cref="IngestDocumentResult.GraphIndexed"/>.
	/// </summary>
	private async Task<bool> TryIndexCorpusGraphAsync(
		IReadOnlyList<DocumentChunk> chunks, string jobId, CancellationToken cancellationToken)
	{
		try
		{
			var graphRagService = _serviceProvider.GetService<IGraphRagService>();
			if (graphRagService is null)
			{
				_logger.LogWarning(
					"RAG ingestion {JobId}: GraphRag:IndexOnIngest is enabled but IGraphRagService is not " +
					"registered — the graph database backend is disabled " +
					"(AppConfig:AI:Rag:GraphDatabase:Enabled=false). Skipping corpus graph indexing.",
					jobId);
				return false;
			}

			await graphRagService.IndexCorpusAsync(chunks, cancellationToken);
			_logger.LogInformation(
				"RAG ingestion {JobId}: corpus graph indexed ({ChunkCount} chunks)",
				jobId, chunks.Count);
			return true;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			_logger.LogInformation(
				"RAG ingestion {JobId}: corpus graph indexing cancelled; vector and BM25 indexes remain committed",
				jobId);
			return false;
		}
		catch (Exception ex)
		{
			_logger.LogError(
				ex,
				"RAG ingestion {JobId}: corpus graph indexing failed; vector and BM25 indexes remain committed",
				jobId);
			return false;
		}
	}

	/// <summary>
	/// Best-effort knowledge-graph enrichment stage
	/// (<c>GraphRagConfig.EnrichKnowledgeGraphOnIngest</c>). Runs the <c>"kg-ingestion"</c>
	/// keyed workflow (extract entities → stamp provenance → store graph) over the ingested
	/// chunks; extracted entities are persisted through the root
	/// <c>IKnowledgeGraphStore</c> registration, i.e. the tenant-isolation and compliance
	/// decorator chain. Never throws — including on cancellation — because the vector and
	/// BM25 writes have already committed; the outcome is reported on
	/// <see cref="IngestDocumentResult.KnowledgeGraphEnriched"/>.
	/// </summary>
	private async Task<bool> TryEnrichKnowledgeGraphAsync(
		IReadOnlyList<DocumentChunk> chunks, string jobId, CancellationToken cancellationToken)
	{
		try
		{
			var workflow = _serviceProvider.GetRequiredKeyedService<Workflow>("kg-ingestion");
			await using var run = await InProcessExecution.RunAsync(
				workflow, new KgIngestionInput(chunks), cancellationToken: cancellationToken);

			KgIngestionResult? outcome = null;
			foreach (var evt in run.NewEvents)
			{
				if (evt is WorkflowOutputEvent output && output.Is<KgIngestionResult>(out var result))
				{
					outcome = result;
				}
			}

			if (outcome is null)
			{
				_logger.LogWarning(
					"RAG ingestion {JobId}: kg-ingestion workflow completed without producing an output event; " +
					"treating knowledge graph enrichment as failed",
					jobId);
				return false;
			}

			_logger.LogInformation(
				"RAG ingestion {JobId}: knowledge graph enriched ({NodesStored} nodes, {EdgesStored} edges " +
				"from {ChunksProcessed} chunks)",
				jobId, outcome.NodesStored, outcome.EdgesStored, outcome.ChunksProcessed);
			return true;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			_logger.LogInformation(
				"RAG ingestion {JobId}: knowledge graph enrichment cancelled; vector and BM25 indexes remain committed",
				jobId);
			return false;
		}
		catch (Exception ex)
		{
			_logger.LogError(
				ex,
				"RAG ingestion {JobId}: knowledge graph enrichment failed; vector and BM25 indexes remain committed",
				jobId);
			return false;
		}
	}
}
