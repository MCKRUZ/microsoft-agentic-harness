using System.Diagnostics;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Enums;
using Domain.AI.RAG.Models;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.RAG.Orchestration;

public sealed partial class RagOrchestrator
{
    private async Task<RagAssembledContext> ExecuteMultiHopPipelineAsync(
        string query, int topKPerHop, string? collectionName,
        CancellationToken cancellationToken)
    {
        var retriever = _iterativeRetriever
            ?? throw new InvalidOperationException("IterativeRetriever is not configured but multi-hop was requested.");

        using var activity = ActivitySource.StartActivity("rag.orchestrator.multi_hop_pipeline");
        var ragConfig = _configMonitor.CurrentValue.AI.Rag;

        _logger.LogInformation("Entering multi-hop pipeline for complex query");

        // Step 1: Iterative retrieval
        var iterativeResult = await retriever.RetrieveIterativelyAsync(
            query, topKPerHop, collectionName, cancellationToken);

        activity?.SetTag("rag.multi_hop.hop_count", iterativeResult.Hops.Count);
        activity?.SetTag("rag.multi_hop.total_tokens", iterativeResult.TotalTokensUsed);
        activity?.SetTag("rag.multi_hop.budget_exhausted", iterativeResult.BudgetExhausted);

        if (iterativeResult.AggregatedResults.Count == 0)
        {
            _logger.LogWarning("Multi-hop retrieval returned 0 results");
            return CreateEmptyContext("No relevant documents found after multi-hop retrieval.");
        }

        // Step 2: Rerank the aggregated results
        var reranked = await _reranker.RerankAsync(
            query, iterativeResult.AggregatedResults, topKPerHop, cancellationToken);

        // Step 3: Assemble context
        var assembled = await _contextAssembler.AssembleAsync(
            reranked, DefaultMaxTokens, cancellationToken);

        // Step 4: Faithfulness evaluation (if enabled)
        if (ragConfig.Faithfulness.Enabled && _faithfulnessEvaluator is not null)
        {
            var faithfulness = await _faithfulnessEvaluator.EvaluateAsync(
                assembled.AssembledText, reranked, cancellationToken);

            activity?.SetTag("rag.faithfulness.score", faithfulness.Score);
            activity?.SetTag("rag.faithfulness.is_faithful", faithfulness.IsFaithful);
            activity?.SetTag("rag.faithfulness.hallucinated_count", faithfulness.HallucinatedClaims.Count);

            var isUnfaithful = faithfulness.Score <= ragConfig.Faithfulness.HallucinationThreshold;

            if (isUnfaithful)
            {
                _logger.LogWarning(
                    "Faithfulness below threshold: score={Score:F2} < {Threshold:F2}, hallucinated={Count} claims. Triggering CRAG refinement.",
                    faithfulness.Score, ragConfig.Faithfulness.HallucinationThreshold, faithfulness.HallucinatedClaims.Count);

                var cragEval = await _cragEvaluator.EvaluateAsync(
                    query, iterativeResult.AggregatedResults, cancellationToken);

                if (cragEval.Action == CorrectionAction.Accept || cragEval.Action == CorrectionAction.Refine)
                {
                    var filtered = FilterWeakChunks(reranked, cragEval.WeakChunkIds);
                    return await _contextAssembler.AssembleAsync(
                        filtered, DefaultMaxTokens, cancellationToken);
                }

                _logger.LogWarning("CRAG also rejected after faithfulness failure; returning best available");
            }
            else
            {
                _logger.LogInformation(
                    "Faithfulness check passed: score={Score:F2}, {Supported} supported claims",
                    faithfulness.Score, faithfulness.SupportedClaims.Count);
            }
        }

        return assembled;
    }

    private static IReadOnlyList<RerankedResult> FilterWeakChunks(
        IReadOnlyList<RerankedResult> results,
        IReadOnlyList<string> weakChunkIds)
    {
        if (weakChunkIds.Count == 0) return results;

        var weakSet = weakChunkIds.ToHashSet();
        return results
            .Where(r => !weakSet.Contains(r.RetrievalResult.Chunk.Id))
            .ToList();
    }
}
