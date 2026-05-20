using System.Text.Json;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.RAG.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.RAG.QueryTransform;

/// <summary>
/// LLM-based query decomposer that breaks complex multi-part queries into ordered
/// sub-queries with dependency tracking. Uses few-shot prompting to produce structured
/// decompositions. Falls back to a single sub-query wrapping the original on any failure.
/// </summary>
/// <remarks>
/// <para>
/// The decomposer identifies independent sub-questions that can be answered via separate
/// retrieval passes, and establishes dependency ordering when a sub-question requires
/// context from a prior answer. This enables the <see cref="IIterativeRetriever"/> to
/// execute sub-queries in the correct order and inject prior hop results as context.
/// </para>
/// </remarks>
public sealed class QueryDecomposer : IQueryDecomposer
{
    private readonly IRagModelRouter _modelRouter;
    private readonly ILogger<QueryDecomposer> _logger;

    private const string SystemPrompt = """
        You are a query decomposition engine for a RAG (Retrieval-Augmented Generation) system.
        Your job is to break complex, multi-part questions into smaller, independently-retrievable sub-queries.

        **Rules:**
        1. Each sub-query should target a single concept or fact that can be answered by one retrieval pass.
        2. Assign a 1-based sequential order to each sub-query.
        3. If a sub-query depends on the answer to a prior sub-query, set "depends_on" to an array of those order numbers.
        4. If the query is already simple and cannot be decomposed, return a single sub-query with the original text.
        5. Keep sub-query text concise but self-contained (understandable without seeing other sub-queries).

        **Examples:**

        User: "What chunking strategies are available and how do they compare for code files?"
        Response:
        {
            "sub_queries": [
                {"text": "What chunking strategies are available in the RAG pipeline?", "order": 1, "depends_on": []},
                {"text": "How do the chunking strategies compare for code files?", "order": 2, "depends_on": [1]}
            ]
        }

        User: "Based on the architecture docs and the deployment guide, what changes are needed to support multi-tenant GraphRAG?"
        Response:
        {
            "sub_queries": [
                {"text": "What is the current GraphRAG architecture?", "order": 1, "depends_on": []},
                {"text": "What does the deployment guide specify about multi-tenancy?", "order": 2, "depends_on": []},
                {"text": "What changes are needed to support multi-tenant GraphRAG given the architecture and deployment constraints?", "order": 3, "depends_on": [1, 2]}
            ]
        }

        User: "What is the default topK value?"
        Response:
        {
            "sub_queries": [
                {"text": "What is the default topK value?", "order": 1, "depends_on": []}
            ]
        }

        Respond with JSON only. No explanation, no markdown fences.
        """;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryDecomposer"/> class.
    /// </summary>
    /// <param name="modelRouter">Model router for resolving the LLM client.</param>
    /// <param name="logger">Logger for decomposition diagnostics.</param>
    public QueryDecomposer(
        IRagModelRouter modelRouter,
        ILogger<QueryDecomposer> logger)
    {
        _modelRouter = modelRouter;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DecomposedQuery> DecomposeAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var client = _modelRouter.GetClientForOperation("query_decomposition");
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, SystemPrompt),
                new(ChatRole.User, query),
            };

            var response = await client.GetResponseAsync(messages, cancellationToken: cancellationToken);
            var content = response.Text?.Trim() ?? string.Empty;

            return ParseDecomposition(query, content);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Query decomposition failed, falling back to single sub-query");
            return CreateFallback(query);
        }
    }

    private DecomposedQuery ParseDecomposition(string originalQuery, string content)
    {
        try
        {
            var jsonStart = content.IndexOf('{');
            var jsonEnd = content.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd < 0 || jsonEnd <= jsonStart)
                return CreateFallback(originalQuery);

            var json = content[jsonStart..(jsonEnd + 1)];
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("sub_queries", out var subQueriesElement)
                || subQueriesElement.ValueKind != JsonValueKind.Array)
                return CreateFallback(originalQuery);

            var subQueries = new List<SubQuery>();
            foreach (var item in subQueriesElement.EnumerateArray())
            {
                var text = item.GetProperty("text").GetString() ?? string.Empty;
                var order = item.TryGetProperty("order", out var orderProp)
                    ? orderProp.GetInt32()
                    : subQueries.Count + 1;

                var dependsOn = new List<int>();
                if (item.TryGetProperty("depends_on", out var depsProp)
                    && depsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var dep in depsProp.EnumerateArray())
                        dependsOn.Add(dep.GetInt32());
                }

                subQueries.Add(new SubQuery
                {
                    Text = text,
                    Order = order,
                    DependsOnOrders = dependsOn,
                });
            }

            if (subQueries.Count == 0)
                return CreateFallback(originalQuery);

            subQueries.Sort((a, b) => a.Order.CompareTo(b.Order));

            _logger.LogDebug(
                "Decomposed query into {Count} sub-queries, sequential={Sequential}",
                subQueries.Count,
                subQueries.Any(sq => sq.DependsOnOrders.Count > 0));

            return new DecomposedQuery
            {
                OriginalQuery = originalQuery,
                SubQueries = subQueries,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse decomposition JSON, falling back to single sub-query");
            return CreateFallback(originalQuery);
        }
    }

    private static DecomposedQuery CreateFallback(string originalQuery) =>
        new()
        {
            OriginalQuery = originalQuery,
            SubQueries =
            [
                new SubQuery
                {
                    Text = originalQuery,
                    Order = 1,
                    DependsOnOrders = [],
                }
            ],
        };
}
