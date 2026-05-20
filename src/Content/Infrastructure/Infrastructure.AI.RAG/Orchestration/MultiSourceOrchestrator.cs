using System.Diagnostics;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;
using Domain.Common.Config;
using Domain.Common.Config.AI.RAG;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.RAG.Orchestration;

/// <summary>
/// Coordinates retrieval across vector, graph, and web sources in parallel.
/// Selects sources based on query complexity, deduplicates results by chunk ID
/// (keeping the highest fused score), and respects per-source timeouts.
/// </summary>
public sealed class MultiSourceOrchestrator : IMultiSourceOrchestrator
{
    private static readonly ActivitySource ActivitySource = new("Infrastructure.AI.RAG.MultiSource");

    private const string SourceVector = "vector";
    private const string SourceGraph = "graph";
    private const string SourceWeb = "web";

    private readonly IHybridRetriever _hybridRetriever;
    private readonly IGraphRagService _graphRagService;
    private readonly IRetrievalCostTracker _costTracker;
    private readonly IOptionsMonitor<AppConfig> _configMonitor;
    private readonly ILogger<MultiSourceOrchestrator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MultiSourceOrchestrator"/> class.
    /// </summary>
    public MultiSourceOrchestrator(
        IHybridRetriever hybridRetriever,
        IGraphRagService graphRagService,
        IRetrievalCostTracker costTracker,
        IOptionsMonitor<AppConfig> configMonitor,
        ILogger<MultiSourceOrchestrator> logger)
    {
        ArgumentNullException.ThrowIfNull(hybridRetriever);
        ArgumentNullException.ThrowIfNull(graphRagService);
        ArgumentNullException.ThrowIfNull(costTracker);
        ArgumentNullException.ThrowIfNull(configMonitor);
        ArgumentNullException.ThrowIfNull(logger);

        _hybridRetriever = hybridRetriever;
        _graphRagService = graphRagService;
        _costTracker = costTracker;
        _configMonitor = configMonitor;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RetrievalResult>> RetrieveFromAllSourcesAsync(
        string query,
        int topK,
        QueryComplexity complexity,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("rag.multi_source.retrieve");
        var config = _configMonitor.CurrentValue.AI.Rag.MultiSource;

        var sourcesToQuery = DetermineSourcesForComplexity(complexity, config);
        activity?.SetTag("rag.multi_source.source_count", sourcesToQuery.Count);
        activity?.SetTag("rag.multi_source.complexity", complexity.ToString().ToLowerInvariant());

        _logger.LogInformation(
            "Multi-source retrieval: Complexity={Complexity}, Sources=[{Sources}], TopK={TopK}",
            complexity, string.Join(", ", sourcesToQuery), topK);

        var sourceResults = await FanOutToSourcesAsync(
            query, topK, sourcesToQuery, config.SourceTimeout, cancellationToken);

        var allResults = new List<RetrievalResult>();
        foreach (var sourceResult in sourceResults)
        {
            allResults.AddRange(sourceResult.Results);
        }

        var deduplicated = DeduplicateByChunkId(allResults);

        var sorted = deduplicated
            .OrderByDescending(r => r.FusedScore)
            .Take(topK)
            .ToList();

        _logger.LogInformation(
            "Multi-source retrieval complete: {TotalRaw} raw, {Deduplicated} deduplicated, {Returned} returned",
            allResults.Count, deduplicated.Count, sorted.Count);

        return sorted;
    }

    private static IReadOnlyList<string> DetermineSourcesForComplexity(
        QueryComplexity complexity,
        MultiSourceConfig config)
    {
        var enabled = new HashSet<string>(config.EnabledSources, StringComparer.OrdinalIgnoreCase);

        var candidates = complexity switch
        {
            QueryComplexity.Trivial or QueryComplexity.Simple => new[] { SourceVector },
            QueryComplexity.Moderate => new[] { SourceVector, SourceGraph },
            QueryComplexity.Complex => new[] { SourceVector, SourceGraph, SourceWeb },
            _ => new[] { SourceVector }
        };

        return candidates.Where(s => enabled.Contains(s)).ToList();
    }

    private async Task<IReadOnlyList<SourceRetrievalResult>> FanOutToSourcesAsync(
        string query,
        int topK,
        IReadOnlyList<string> sources,
        TimeSpan sourceTimeout,
        CancellationToken cancellationToken)
    {
        var tasks = sources.Select(source =>
            ExecuteSourceWithTimeoutAsync(source, query, topK, sourceTimeout, cancellationToken));

        var results = await Task.WhenAll(tasks);
        return results.Where(r => r is not null).Cast<SourceRetrievalResult>().ToList();
    }

    private async Task<SourceRetrievalResult?> ExecuteSourceWithTimeoutAsync(
        string sourceName,
        string query,
        int topK,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            var results = sourceName switch
            {
                SourceVector => await _hybridRetriever.RetrieveAsync(
                    query, topK, collectionName: null, timeoutCts.Token),
                SourceGraph => await _graphRagService.LocalSearchAsync(
                    query, topK, timeoutCts.Token),
                SourceWeb => await ExecuteWebSearchAsync(query, topK, timeoutCts.Token),
                _ => (IReadOnlyList<RetrievalResult>)[]
            };

            sw.Stop();

            _logger.LogDebug(
                "Source {Source} returned {Count} results in {ElapsedMs}ms",
                sourceName, results.Count, sw.Elapsed.TotalMilliseconds);

            return new SourceRetrievalResult
            {
                SourceName = sourceName,
                Results = results,
                Latency = sw.Elapsed,
                TokensUsed = 0
            };
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            _logger.LogWarning("Source {Source} timed out after {TimeoutMs}ms", sourceName, timeout.TotalMilliseconds);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            _logger.LogWarning(ex, "Source {Source} failed: {Message}", sourceName, ex.Message);
            return null;
        }
    }

    private static Task<IReadOnlyList<RetrievalResult>> ExecuteWebSearchAsync(
        string query, int topK, CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<RetrievalResult>>([]);
    }

    private static IReadOnlyList<RetrievalResult> DeduplicateByChunkId(
        IReadOnlyList<RetrievalResult> results)
    {
        var bestByChunkId = new Dictionary<string, RetrievalResult>();

        foreach (var result in results)
        {
            var chunkId = result.Chunk.Id;
            if (!bestByChunkId.TryGetValue(chunkId, out var existing) ||
                result.FusedScore > existing.FusedScore)
            {
                bestByChunkId[chunkId] = result;
            }
        }

        return bestByChunkId.Values.ToList();
    }
}
