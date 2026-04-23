using System.Diagnostics;
using Application.AI.Common.Interfaces.RAG;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.RAG.QueryTransform;

/// <summary>
/// Generates diverse reformulations of a query for RAG-Fusion retrieval.
/// Each variant is independently embedded and searched; results are merged
/// via Reciprocal Rank Fusion (RRF) to improve recall for ambiguous or
/// underspecified queries. The number of variants is controlled by
/// <c>AppConfig:AI:Rag:QueryTransform:FusionVariantCount</c> (default 3).
/// </summary>
/// <remarks>
/// <para>
/// The returned list always starts with the original query followed by
/// the LLM-generated variants. If the LLM call fails, only the original
/// query is returned so retrieval can proceed without transformation.
/// </para>
/// </remarks>
public sealed class RagFusionTransformer : IQueryTransformer
{
    private static readonly ActivitySource ActivitySource = new("Infrastructure.AI.RAG.QueryTransform");

    private const string FusionPromptTemplate =
        "Generate {0} diverse reformulations of this search query. Each reformulation should approach the topic from a different angle or use different terminology. Return one per line, with no numbering or bullet points.\n\nQuery: {1}";

    private readonly IRagModelRouter _modelRouter;
    private readonly IOptionsMonitor<AppConfig> _configMonitor;
    private readonly ILogger<RagFusionTransformer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RagFusionTransformer"/> class.
    /// </summary>
    /// <param name="modelRouter">Model router for selecting an economy-tier chat client.</param>
    /// <param name="configMonitor">Configuration monitor for reading fusion variant count.</param>
    /// <param name="logger">Logger for recording transformation outcomes.</param>
    public RagFusionTransformer(
        IRagModelRouter modelRouter,
        IOptionsMonitor<AppConfig> configMonitor,
        ILogger<RagFusionTransformer> logger)
    {
        _modelRouter = modelRouter;
        _configMonitor = configMonitor;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> TransformAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        using var activity = ActivitySource.StartActivity("rag.query_transform.rag_fusion");

        var variantCount = _configMonitor.CurrentValue.AI.Rag.QueryTransform.FusionVariantCount;
        var tier = _modelRouter.GetTierForOperation("rag_fusion");

        activity?.SetTag(RagConventions.ModelTier, tier);
        activity?.SetTag(RagConventions.ModelOperation, "rag_fusion");
        activity?.SetTag(RagConventions.FusionVariantCount, variantCount);

        try
        {
            var chatClient = _modelRouter.GetClientForOperation("rag_fusion");
            var prompt = string.Format(FusionPromptTemplate, variantCount, query);

            var response = await chatClient.GetResponseAsync(prompt, cancellationToken: cancellationToken);
            var responseText = response.Text?.Trim() ?? string.Empty;

            var variants = ParseVariants(responseText);
            var results = new List<string>(variantCount + 1) { query };
            results.AddRange(variants);

            _logger.LogInformation(
                "RAG-Fusion generated {VariantCount} query variants for retrieval",
                variants.Count);

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RAG-Fusion transformation failed; returning original query only");
            return [query];
        }
    }

    /// <summary>
    /// Parses line-delimited variants from the LLM response, filtering out
    /// empty lines and any lines that look like numbering artifacts.
    /// </summary>
    private static IReadOnlyList<string> ParseVariants(string responseText)
    {
        var lines = responseText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var variants = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            // Strip leading numbering (e.g., "1. ", "- ")
            var cleaned = StripLeadingNumbering(line);
            if (!string.IsNullOrWhiteSpace(cleaned))
                variants.Add(cleaned);
        }

        return variants;
    }

    /// <summary>
    /// Removes common numbering prefixes from a line (e.g., "1. ", "- ", "* ").
    /// </summary>
    private static string StripLeadingNumbering(string line)
    {
        var span = line.AsSpan();

        // Skip leading digits followed by ". " or ") "
        var i = 0;
        while (i < span.Length && char.IsDigit(span[i]))
            i++;

        if (i > 0 && i < span.Length && (span[i] == '.' || span[i] == ')'))
        {
            i++;
            if (i < span.Length && span[i] == ' ')
                i++;
            return span[i..].ToString();
        }

        // Skip leading "- " or "* "
        if (span.Length >= 2 && (span[0] == '-' || span[0] == '*') && span[1] == ' ')
            return span[2..].ToString();

        return line;
    }
}
