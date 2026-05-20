using System.Diagnostics;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;
using Domain.Common.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.RAG.Orchestration;

/// <summary>
/// Coordinates retrieval across multiple sources in parallel.
/// Sources are resolved by key from the DI container, enabling pluggable source registration
/// without modifying the orchestrator. Selects sources based on config-driven complexity
/// mappings, deduplicates results by chunk ID (keeping the highest fused score), and
/// respects per-source timeouts.
/// </summary>
public sealed class MultiSourceOrchestrator : IMultiSourceOrchestrator
{
    private static readonly ActivitySource ActivitySource = new("Infrastructure.AI.RAG.MultiSource");

    private readonly IServiceProvider _serviceProvider;
    private readonly IRetrievalCostTracker _costTracker;
    private readonly IOptionsMonitor<AppConfig> _configMonitor;
    private readonly ILogger<MultiSourceOrchestrator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MultiSourceOrchestrator"/> class.
    /// </summary>
    /// <param name="serviceProvider">Used to resolve <see cref="IRetrievalSource"/> implementations by key.</param>
    /// <param name="costTracker">Tracks retrieval cost and token usage across sources.</param>
    /// <param name="configMonitor">Live config access for source enablement and complexity mappings.</param>
    /// <param name="logger">Logger for retrieval lifecycle events and warnings.</param>
    public MultiSourceOrchestrator(
        IServiceProvider serviceProvider,
        IRetrievalCostTracker costTracker,
        IOptionsMonitor<AppConfig> configMonitor,
        ILogger<MultiSourceOrchestrator> logger)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(costTracker);
        ArgumentNullException.ThrowIfNull(configMonitor);
        ArgumentNullException.ThrowIfNull(logger);

        _serviceProvider = serviceProvider;
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

        var sourcesToQuery = DetermineSourcesForComplexity(complexity);
        activity?.SetTag("rag.multi_source.source_count", sourcesToQuery.Count);
        activity?.SetTag("rag.multi_source.complexity", complexity.ToString().ToLowerInvariant());

        _logger.LogInformation(
            "Multi-source retrieval: Complexity={Complexity}, Sources=[{Sources}], TopK={TopK}",
            complexity, string.Join(", ", sourcesToQuery), topK);

        var sourceResults = await FanOutToSourcesAsync(
            query, topK, complexity, sourcesToQuery, config.SourceTimeout, cancellationToken);

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

    private IReadOnlyList<string> DetermineSourcesForComplexity(QueryComplexity complexity)
    {
        var config = _configMonitor.CurrentValue.AI.Rag.MultiSource;
        var complexityKey = complexity.ToString();

        if (!config.SourcesByComplexity.TryGetValue(complexityKey, out var candidates))
            candidates = ["vector"];

        return candidates
            .Where(s => config.EnabledSources.Contains(s, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    private async Task<IReadOnlyList<SourceRetrievalResult>> FanOutToSourcesAsync(
        string query,
        int topK,
        QueryComplexity complexity,
        IReadOnlyList<string> sources,
        TimeSpan sourceTimeout,
        CancellationToken cancellationToken)
    {
        var tasks = sources.Select(source =>
            ExecuteSourceWithTimeoutAsync(source, query, topK, complexity, sourceTimeout, cancellationToken));

        var results = await Task.WhenAll(tasks);
        return results.Where(r => r is not null).Cast<SourceRetrievalResult>().ToList();
    }

    private async Task<SourceRetrievalResult?> ExecuteSourceWithTimeoutAsync(
        string sourceName,
        string query,
        int topK,
        QueryComplexity complexity,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var source = _serviceProvider.GetKeyedService<IRetrievalSource>(sourceName);
        if (source is null)
        {
            _logger.LogWarning("Retrieval source '{SourceName}' is enabled but not registered in DI", sourceName);
            return null;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            return await source.RetrieveAsync(query, topK, complexity, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Retrieval source '{SourceName}' timed out after {Timeout}ms",
                sourceName, timeout.TotalMilliseconds);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Retrieval source '{SourceName}' failed", sourceName);
            return null;
        }
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
