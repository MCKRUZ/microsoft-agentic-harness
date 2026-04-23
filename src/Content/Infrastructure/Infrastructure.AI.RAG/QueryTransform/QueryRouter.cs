using System.Diagnostics;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.RAG.QueryTransform;

/// <summary>
/// Orchestrates the query transformation chain by combining classification
/// and transformation steps. First classifies the query (if enabled) to
/// determine its type and retrieval strategy, then selects and executes
/// the appropriate <see cref="IQueryTransformer"/> based on the
/// classification result and configuration.
/// </summary>
/// <remarks>
/// <para><strong>Transformer selection logic:</strong></para>
/// <list type="bullet">
///   <item>MultiHop or Comparative + RAG-Fusion enabled → <c>"rag_fusion"</c> transformer</item>
///   <item>Low confidence (below 0.7) → <c>"hyde"</c> transformer to bridge semantic gap</item>
///   <item>Otherwise → no transformation (original query returned as-is)</item>
/// </list>
/// </remarks>
public sealed class QueryRouter
{
    private static readonly ActivitySource ActivitySource = new("Infrastructure.AI.RAG.QueryTransform");
    private const double LowConfidenceThreshold = 0.7;

    private readonly IQueryClassifier _classifier;
    private readonly IServiceProvider _serviceProvider;
    private readonly IOptionsMonitor<AppConfig> _configMonitor;
    private readonly ILogger<QueryRouter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryRouter"/> class.
    /// </summary>
    /// <param name="classifier">Query classifier for determining query type.</param>
    /// <param name="serviceProvider">Service provider for resolving keyed transformers.</param>
    /// <param name="configMonitor">Configuration monitor for query transform settings.</param>
    /// <param name="logger">Logger for recording routing decisions.</param>
    public QueryRouter(
        IQueryClassifier classifier,
        IServiceProvider serviceProvider,
        IOptionsMonitor<AppConfig> configMonitor,
        ILogger<QueryRouter> logger)
    {
        _classifier = classifier;
        _serviceProvider = serviceProvider;
        _configMonitor = configMonitor;
        _logger = logger;
    }

    /// <summary>
    /// Routes a query through classification and transformation steps,
    /// returning the classification result and the transformed query variants.
    /// </summary>
    /// <param name="query">The original user query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A tuple of the classification result and the list of transformed queries.
    /// When classification is disabled, returns a default SimpleLookup classification.
    /// The query list always contains at least one element.
    /// </returns>
    public async Task<(QueryClassification Classification, IReadOnlyList<string> TransformedQueries)> RouteAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("rag.query_router");

        var config = _configMonitor.CurrentValue.AI.Rag.QueryTransform;

        // Step 1: Classify the query (if enabled)
        var classification = config.EnableClassification
            ? await _classifier.ClassifyAsync(query, cancellationToken)
            : CreateDefaultClassification();

        activity?.SetTag(RagConventions.QueryType, classification.Type.ToString().ToLowerInvariant());
        activity?.SetTag(RagConventions.RetrievalStrategy, classification.Strategy.ToString().ToLowerInvariant());

        // Step 2: Select and execute transformer
        var transformerKey = SelectTransformer(classification, config);

        if (transformerKey is null)
        {
            _logger.LogDebug(
                "No query transformation selected for {QueryType} (confidence: {Confidence:F2})",
                classification.Type, classification.Confidence);
            return (classification, new[] { query });
        }

        activity?.SetTag("rag.query_transform.strategy", transformerKey);

        var transformer = _serviceProvider.GetRequiredKeyedService<IQueryTransformer>(transformerKey);
        var transformedQueries = await transformer.TransformAsync(query, cancellationToken);

        _logger.LogInformation(
            "Query routed through '{TransformerKey}' transformer: {OriginalQuery} → {VariantCount} queries",
            transformerKey, query, transformedQueries.Count);

        return (classification, transformedQueries);
    }

    /// <summary>
    /// Selects the appropriate transformer key based on classification and config.
    /// Returns <c>null</c> when no transformation is needed.
    /// </summary>
    private static string? SelectTransformer(
        QueryClassification classification,
        Domain.Common.Config.AI.RAG.QueryTransformConfig config)
    {
        // MultiHop/Comparative queries benefit from RAG-Fusion
        var isMultiQueryType = classification.Type is QueryType.MultiHop or QueryType.Comparative;
        if (isMultiQueryType && config.EnableRagFusion)
            return "rag_fusion";

        // Low confidence → HyDE bridges the semantic gap
        if (classification.Confidence < LowConfidenceThreshold && config.EnableHyde)
            return "hyde";

        return null;
    }

    /// <summary>
    /// Creates a default classification for when classification is disabled.
    /// </summary>
    private static QueryClassification CreateDefaultClassification() => new()
    {
        Type = QueryType.SimpleLookup,
        Strategy = RetrievalStrategy.HybridVectorBm25,
        Confidence = 1.0,
        Reasoning = "Classification disabled; using default strategy"
    };
}
