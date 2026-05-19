using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;
using Domain.Common.Config;
using Domain.Common.Config.AI.RAG;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.RAG.Orchestration;

/// <summary>
/// Determines the retrieval execution path based on query complexity classification.
/// When confidence is below threshold, falls back to Moderate (full pipeline) for safety.
/// When routing is disabled, always returns full pipeline parameters.
/// </summary>
public sealed class RetrievalDecisionGate : IRetrievalDecisionGate
{
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<RetrievalDecisionGate> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="RetrievalDecisionGate"/>.
    /// </summary>
    /// <param name="config">Application configuration monitor.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public RetrievalDecisionGate(
        IOptionsMonitor<AppConfig> config,
        ILogger<RetrievalDecisionGate> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public RetrievalDecision Decide(ComplexityClassification classification, int? requestedTopK = null)
    {
        var ragConfig = _config.CurrentValue.AI.Rag;
        var routingConfig = ragConfig.ComplexityRouting;

        if (!routingConfig.Enabled)
            return CreateFullPipelineDecision(ragConfig, requestedTopK);

        var effectiveComplexity = classification.Confidence < routingConfig.ConfidenceThreshold
            ? QueryComplexity.Moderate
            : classification.Complexity;

        if (effectiveComplexity != classification.Complexity)
        {
            _logger.LogDebug(
                "Complexity downgraded from {Original} to Moderate due to low confidence {Confidence:F2}",
                classification.Complexity,
                classification.Confidence);
        }

        return effectiveComplexity switch
        {
            QueryComplexity.Trivial => new RetrievalDecision
            {
                SkipRetrieval = true,
                TopK = 0,
                UseReranking = false,
                UseCragEvaluation = false,
                Complexity = QueryComplexity.Trivial,
            },
            QueryComplexity.Simple => new RetrievalDecision
            {
                SkipRetrieval = false,
                TopK = requestedTopK ?? routingConfig.SimpleTopK,
                UseReranking = !routingConfig.SkipRerankForSimple,
                UseCragEvaluation = !routingConfig.SkipCragForSimple,
                Complexity = QueryComplexity.Simple,
            },
            QueryComplexity.Complex => new RetrievalDecision
            {
                SkipRetrieval = false,
                TopK = requestedTopK ?? routingConfig.ComplexTopK,
                UseReranking = true,
                UseCragEvaluation = true,
                Complexity = QueryComplexity.Complex,
            },
            _ => CreateFullPipelineDecision(ragConfig, requestedTopK),
        };
    }

    private static RetrievalDecision CreateFullPipelineDecision(RagConfig ragConfig, int? requestedTopK) =>
        new()
        {
            SkipRetrieval = false,
            TopK = requestedTopK ?? ragConfig.ComplexityRouting.ModerateTopK ?? ragConfig.Retrieval.TopK,
            UseReranking = true,
            UseCragEvaluation = true,
            Complexity = QueryComplexity.Moderate,
        };
}
