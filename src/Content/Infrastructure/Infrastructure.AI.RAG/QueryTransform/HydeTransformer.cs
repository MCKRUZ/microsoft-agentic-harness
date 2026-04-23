using System.Diagnostics;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.Telemetry.Conventions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.RAG.QueryTransform;

/// <summary>
/// Generates a Hypothetical Document Embedding (HyDE) for a query. Instead
/// of embedding the raw question, generates a short hypothetical answer
/// passage (100-200 words) and returns it for embedding. This bridges the
/// semantic gap between question-style queries and document-style content
/// in the vector store, improving retrieval when the query phrasing differs
/// significantly from the stored passages.
/// </summary>
/// <remarks>
/// <para>
/// Returns a single-element list containing the hypothetical document. The
/// original query is intentionally excluded — the caller is responsible for
/// deciding whether to embed both the original and the hypothetical answer.
/// </para>
/// <para>
/// When the LLM call fails, falls back to returning the original query so
/// retrieval can proceed without transformation.
/// </para>
/// </remarks>
public sealed class HydeTransformer : IQueryTransformer
{
    private static readonly ActivitySource ActivitySource = new("Infrastructure.AI.RAG.QueryTransform");

    private const string HydePrompt =
        "Write a short passage (100-200 words) that would be the ideal answer to this question. Do not include any preamble.\n\nQuestion: {0}";

    private readonly IRagModelRouter _modelRouter;
    private readonly ILogger<HydeTransformer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HydeTransformer"/> class.
    /// </summary>
    /// <param name="modelRouter">Model router for selecting the appropriate chat client.</param>
    /// <param name="logger">Logger for recording HyDE generation outcomes.</param>
    public HydeTransformer(
        IRagModelRouter modelRouter,
        ILogger<HydeTransformer> logger)
    {
        _modelRouter = modelRouter;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> TransformAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("rag.query_transform.hyde");

        var tier = _modelRouter.GetTierForOperation("hyde_generation");
        activity?.SetTag(RagConventions.ModelTier, tier);
        activity?.SetTag(RagConventions.ModelOperation, "hyde_generation");

        try
        {
            var chatClient = _modelRouter.GetClientForOperation("hyde_generation");
            var prompt = string.Format(HydePrompt, query);

            var response = await chatClient.GetResponseAsync(prompt, cancellationToken: cancellationToken);
            var hypotheticalDoc = response.Text?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(hypotheticalDoc))
            {
                _logger.LogWarning("HyDE generation returned empty response; returning original query");
                return [query];
            }

            _logger.LogInformation(
                "HyDE generated hypothetical document ({Length} chars) for query embedding",
                hypotheticalDoc.Length);

            return [hypotheticalDoc];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HyDE generation failed; returning original query");
            return [query];
        }
    }
}
