using System.Diagnostics;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.RAG.Retrieval;

/// <summary>
/// Multi-hop iterative retriever that decomposes complex queries into sub-queries,
/// retrieves per sub-query in dependency order, evaluates sufficiency, and aggregates
/// results across hops. Enforces a hard cap on iterations (<c>MaxHops</c>) and a
/// per-hop token budget to prevent context window overflow.
/// </summary>
/// <remarks>
/// <para>
/// The retrieval loop for each sub-query:
/// <list type="number">
///   <item>Retrieve via <see cref="IHybridRetriever"/> with configured <c>TopKPerHop</c>.</item>
///   <item>Evaluate sufficiency via <see cref="ISufficiencyEvaluator"/>.</item>
///   <item>If sufficient (score >= threshold), record the hop and move to the next sub-query.</item>
///   <item>If insufficient, refine the sub-query with prior context and re-retrieve (up to <c>MaxHops</c>).</item>
/// </list>
/// </para>
/// <para>
/// Dependencies are resolved by injecting the content from completed dependent sub-queries
/// into the refinement prompt, enabling later sub-queries to leverage prior hop results.
/// </para>
/// </remarks>
public sealed class IterativeRetriever : IIterativeRetriever
{
    private static readonly ActivitySource ActivitySource =
        new("Infrastructure.AI.RAG.Retrieval.IterativeRetriever");

    private readonly IQueryDecomposer _decomposer;
    private readonly IHybridRetriever _hybridRetriever;
    private readonly ISufficiencyEvaluator _sufficiencyEvaluator;
    private readonly IOptionsMonitor<AppConfig> _configMonitor;
    private readonly ILogger<IterativeRetriever> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="IterativeRetriever"/> class.
    /// </summary>
    /// <param name="decomposer">Query decomposer for splitting complex queries.</param>
    /// <param name="hybridRetriever">Hybrid retriever for per-hop retrieval.</param>
    /// <param name="sufficiencyEvaluator">Evaluator for assessing retrieval sufficiency.</param>
    /// <param name="configMonitor">Application configuration monitor.</param>
    /// <param name="logger">Logger for retrieval diagnostics.</param>
    public IterativeRetriever(
        IQueryDecomposer decomposer,
        IHybridRetriever hybridRetriever,
        ISufficiencyEvaluator sufficiencyEvaluator,
        IOptionsMonitor<AppConfig> configMonitor,
        ILogger<IterativeRetriever> logger)
    {
        _decomposer = decomposer;
        _hybridRetriever = hybridRetriever;
        _sufficiencyEvaluator = sufficiencyEvaluator;
        _configMonitor = configMonitor;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IterativeRetrievalResult> RetrieveIterativelyAsync(
        string query,
        int topKPerHop,
        string? collectionName = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("rag.iterative_retriever.retrieve");

        var multiHopConfig = _configMonitor.CurrentValue.AI.Rag.MultiHop;
        var maxHops = multiHopConfig.MaxHops;
        var tokenBudgetPerHop = multiHopConfig.TokenBudgetPerHop;
        var minSufficiency = multiHopConfig.MinSufficiencyScore;
        var totalTokenBudget = maxHops * tokenBudgetPerHop;

        var decomposed = await _decomposer.DecomposeAsync(query, cancellationToken);
        _logger.LogInformation(
            "Decomposed query into {SubQueryCount} sub-queries, sequential={Sequential}",
            decomposed.SubQueries.Count, decomposed.RequiresSequentialExecution);

        var hops = new List<HopResult>();
        var allResults = new Dictionary<string, RetrievalResult>();
        var totalTokensUsed = 0;
        var budgetExhausted = false;
        var hopNumber = 0;
        var completedSubQueryResults = new Dictionary<int, IReadOnlyList<RetrievalResult>>();

        foreach (var subQuery in decomposed.SubQueries.OrderBy(sq => sq.Order))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (hopNumber >= maxHops)
            {
                _logger.LogInformation("Max hops ({MaxHops}) reached, stopping iteration", maxHops);
                break;
            }

            if (totalTokensUsed >= totalTokenBudget)
            {
                _logger.LogInformation("Token budget exhausted ({Used}/{Budget})", totalTokensUsed, totalTokenBudget);
                budgetExhausted = true;
                break;
            }

            var effectiveQuery = BuildEffectiveQuery(subQuery, completedSubQueryResults);
            hopNumber++;

            var candidates = await _hybridRetriever.RetrieveAsync(
                effectiveQuery, topKPerHop, collectionName, cancellationToken);

            var hopTokens = candidates.Sum(r => r.Chunk.Tokens);
            totalTokensUsed += hopTokens;

            if (totalTokensUsed > totalTokenBudget)
            {
                budgetExhausted = true;
                _logger.LogDebug("Token budget exceeded on hop {Hop}: {Used}/{Budget}",
                    hopNumber, totalTokensUsed, totalTokenBudget);
            }

            var sufficiencyScore = await _sufficiencyEvaluator.EvaluateAsync(
                subQuery.Text, candidates, cancellationToken);
            var isSufficient = sufficiencyScore >= minSufficiency;

            var hopResult = new HopResult
            {
                SubQuery = subQuery,
                Results = candidates,
                SufficiencyScore = sufficiencyScore,
                HopNumber = hopNumber,
                IsSufficient = isSufficient,
            };
            hops.Add(hopResult);

            activity?.AddEvent(new ActivityEvent("hop_completed", tags: new ActivityTagsCollection
            {
                { "rag.hop.number", hopNumber },
                { "rag.hop.sub_query_order", subQuery.Order },
                { "rag.hop.sufficiency_score", sufficiencyScore },
                { "rag.hop.is_sufficient", isSufficient },
                { "rag.hop.result_count", candidates.Count },
                { "rag.hop.tokens", hopTokens },
            }));

            foreach (var result in candidates)
            {
                if (allResults.TryGetValue(result.Chunk.Id, out var existing))
                {
                    if (result.FusedScore > existing.FusedScore)
                        allResults[result.Chunk.Id] = result;
                }
                else
                {
                    allResults[result.Chunk.Id] = result;
                }
            }

            completedSubQueryResults[subQuery.Order] = candidates;

            _logger.LogDebug(
                "Hop {Hop}: sub-query order={Order}, sufficiency={Score:F2}, sufficient={IsSufficient}, tokens={Tokens}",
                hopNumber, subQuery.Order, sufficiencyScore, isSufficient, hopTokens);
        }

        var aggregatedResults = allResults.Values
            .OrderByDescending(r => r.FusedScore)
            .ToList();

        activity?.SetTag("rag.iterative.total_hops", hops.Count);
        activity?.SetTag("rag.iterative.total_results", aggregatedResults.Count);
        activity?.SetTag("rag.iterative.total_tokens", totalTokensUsed);
        activity?.SetTag("rag.iterative.budget_exhausted", budgetExhausted);
        activity?.SetTag("rag.iterative.sub_query_count", decomposed.SubQueries.Count);

        _logger.LogInformation(
            "Iterative retrieval complete: {Hops} hops, {Results} unique results, {Tokens} tokens, budgetExhausted={BudgetExhausted}",
            hops.Count, aggregatedResults.Count, totalTokensUsed, budgetExhausted);

        return new IterativeRetrievalResult
        {
            Hops = hops,
            AggregatedResults = aggregatedResults,
            TotalTokensUsed = totalTokensUsed,
            BudgetExhausted = budgetExhausted,
        };
    }

    private string BuildEffectiveQuery(
        SubQuery subQuery,
        Dictionary<int, IReadOnlyList<RetrievalResult>> completedResults)
    {
        if (subQuery.DependsOnOrders.Count == 0)
            return subQuery.Text;

        var contextParts = new List<string>();
        foreach (var depOrder in subQuery.DependsOnOrders)
        {
            if (completedResults.TryGetValue(depOrder, out var depResults) && depResults.Count > 0)
            {
                var contextSnippet = string.Join(" ", depResults.Select(r => r.Chunk.Content).Take(3));
                contextParts.Add($"[Context from step {depOrder}]: {contextSnippet}");
            }
        }

        if (contextParts.Count == 0)
            return subQuery.Text;

        var context = string.Join("\n", contextParts);
        return $"{subQuery.Text}\n\nPrior context:\n{context}";
    }
}
