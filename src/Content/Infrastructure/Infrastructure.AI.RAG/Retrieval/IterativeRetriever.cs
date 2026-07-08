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
///   <item>If insufficient, record the hop and move to the next sub-query (up to <c>MaxHops</c> total).</item>
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
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topKPerHop);

        using var activity = ActivitySource.StartActivity("rag.iterative_retriever.retrieve");

        var multiHopConfig = _configMonitor.CurrentValue.AI.Rag.MultiHop;
        var maxHops = multiHopConfig.MaxHops;
        var tokenBudgetPerHop = multiHopConfig.TokenBudgetPerHop;
        var minSufficiency = multiHopConfig.MinSufficiencyScore;
        var maxReRetriesPerHop = multiHopConfig.MaxReRetriesPerHop;
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

            // Bounded re-retrieval: an insufficient hop widens top-k and refines the sub-query,
            // keeping the best-scoring attempt, up to MaxReRetriesPerHop tries or until the
            // per-run token budget runs out. Disabled (behavior-preserving) when the cap is 0.
            if (maxReRetriesPerHop > 0 && sufficiencyScore < minSufficiency)
            {
                var (improved, improvedScore, extraTokens) = await ReRetrieveUntilSufficientAsync(
                    subQuery, effectiveQuery, candidates, sufficiencyScore, topKPerHop, collectionName,
                    maxReRetriesPerHop, minSufficiency, totalTokenBudget - totalTokensUsed,
                    hopNumber, cancellationToken);

                candidates = improved;
                sufficiencyScore = improvedScore;
                totalTokensUsed += extraTokens;
                if (totalTokensUsed > totalTokenBudget)
                    budgetExhausted = true;
            }

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

    /// <summary>
    /// Runs bounded re-retrieval for a hop whose sufficiency verdict is below threshold. Each attempt
    /// widens the top-k (proportional to the attempt number) and appends a refinement instruction to the
    /// effective query, retaining the highest-scoring candidate set seen. Stops after
    /// <paramref name="maxReRetries"/> attempts, once the score clears <paramref name="minSufficiency"/>,
    /// or when the extra tokens consumed reach <paramref name="remainingTokenBudget"/>.
    /// </summary>
    /// <returns>The best candidate set, its sufficiency score, and the total extra tokens consumed.</returns>
    private async Task<(IReadOnlyList<RetrievalResult> Candidates, double Score, int ExtraTokens)>
        ReRetrieveUntilSufficientAsync(
            SubQuery subQuery,
            string effectiveQuery,
            IReadOnlyList<RetrievalResult> initial,
            double initialScore,
            int topKPerHop,
            string? collectionName,
            int maxReRetries,
            double minSufficiency,
            int remainingTokenBudget,
            int hopNumber,
            CancellationToken cancellationToken)
    {
        var bestCandidates = initial;
        var bestScore = initialScore;
        var extraTokens = 0;
        var attempt = 0;

        while (bestScore < minSufficiency
            && attempt < maxReRetries
            && extraTokens < remainingTokenBudget)
        {
            attempt++;
            var widenedTopK = topKPerHop * (attempt + 1);
            var refinedQuery =
                $"{effectiveQuery}\n\n(Insufficient detail; broaden and re-answer: {subQuery.Text})";

            var retry = await _hybridRetriever.RetrieveAsync(
                refinedQuery, widenedTopK, collectionName, cancellationToken);
            extraTokens += retry.Sum(r => r.Chunk.Tokens);

            var retryScore = await _sufficiencyEvaluator.EvaluateAsync(
                subQuery.Text, retry, cancellationToken);

            if (retryScore > bestScore)
            {
                bestCandidates = retry;
                bestScore = retryScore;
            }

            _logger.LogDebug(
                "Hop {Hop} re-retry {Attempt}/{Max}: widenedTopK={TopK}, score={Score:F2}",
                hopNumber, attempt, maxReRetries, widenedTopK, retryScore);
        }

        return (bestCandidates, bestScore, extraTokens);
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
