using System.Text.Json;
using Application.AI.Common.Interfaces.RAG;
using Azure;
using Azure.Search.Documents.KnowledgeBases;
using Azure.Search.Documents.KnowledgeBases.Models;
using Domain.AI.RAG.Models;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.RAG.Retrieval;

/// <summary>
/// An <see cref="IHybridRetriever"/> backed by Azure AI Search <em>agentic retrieval</em>: it
/// delegates the whole query → ranked-results step to a server-side knowledge base
/// (<see cref="KnowledgeBaseRetrievalClient"/>) rather than running the local dense + sparse +
/// RRF pipeline. Opt-in, selected by <c>AppConfig:AI:Rag:AgenticRetrieval:Enabled</c>.
/// </summary>
/// <remarks>
/// <para>
/// Targets the <strong>stable</strong> <c>Azure.Search.Documents</c> GA surface (API version
/// 2026-04-01), which performs parallel extractive retrieval with built-in semantic ranking.
/// The preview-only LLM query-planning and answer-synthesis modes are intentionally not used,
/// so the template takes on no preview dependency.
/// </para>
/// <para>
/// <strong>Impedance note.</strong> The knowledge base returns a ranked grounding payload, not
/// the harness's native <see cref="DocumentChunk"/> shape, so the mapping is necessarily
/// best-effort: the service ranks results server-side (list order is the authority), so a
/// positional proxy score is assigned; <see cref="DocumentChunk.Tokens"/> is estimated; and
/// <see cref="ChunkMetadata.SourceUri"/>/<see cref="ChunkMetadata.CreatedAt"/> are synthesized
/// because the payload carries neither the original document URI nor its ingestion time.
/// </para>
/// <para>
/// Fails soft: a missing configuration or an Azure request failure
/// (<see cref="RequestFailedException"/>) returns no results — mirroring the local hybrid
/// retriever's graceful-degradation contract — rather than throwing into the RAG pipeline.
/// The result-mapping step is exception-safe (it constructs no host-segment URIs and parses
/// JSON defensively), so it never throws regardless of payload or configured name.
/// </para>
/// <para>
/// <strong>Single knowledge base.</strong> This backend always queries the one knowledge base
/// named in configuration. The <c>IHybridRetriever.RetrieveAsync</c> <c>collectionName</c>
/// override is therefore not supported — a non-null value that differs from the configured name
/// is logged and ignored rather than silently retargeting (or wrongly querying) another base.
/// </para>
/// </remarks>
public sealed class AzureKnowledgeBaseRetriever : IHybridRetriever
{
    private readonly KnowledgeBaseRetrievalClient _client;
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly ILogger<AzureKnowledgeBaseRetriever> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureKnowledgeBaseRetriever"/> class.
    /// </summary>
    /// <param name="client">The Azure AI Search knowledge-base retrieval client.</param>
    /// <param name="appConfig">The application configuration monitor.</param>
    /// <param name="logger">The logger.</param>
    public AzureKnowledgeBaseRetriever(
        KnowledgeBaseRetrievalClient client,
        IOptionsMonitor<AppConfig> appConfig,
        ILogger<AzureKnowledgeBaseRetriever> logger)
    {
        _client = client;
        _appConfig = appConfig;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(
        string query,
        int topK,
        string? collectionName = null,
        CancellationToken cancellationToken = default)
    {
        var config = _appConfig.CurrentValue.AI.Rag.AgenticRetrieval;
        if (!config.IsConfigured)
        {
            _logger.LogWarning(
                "Agentic retrieval is enabled but not configured (missing endpoint); returning no results.");
            return [];
        }

        if (string.IsNullOrWhiteSpace(query) || topK <= 0)
            return [];

        if (collectionName is not null && collectionName != config.KnowledgeBaseName)
        {
            _logger.LogWarning(
                "Agentic retrieval ignores collectionName '{Requested}'; it always queries the configured knowledge base '{KnowledgeBase}'.",
                collectionName, config.KnowledgeBaseName);
        }

        KnowledgeBaseRetrievalResponse response;
        try
        {
            var request = new KnowledgeBaseRetrievalRequest();
            request.Intents.Add(new KnowledgeRetrievalSemanticIntent(query));

            var raw = await _client.RetrieveAsync(request, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            response = raw.Value;
        }
        catch (RequestFailedException ex)
        {
            _logger.LogWarning(ex,
                "Azure knowledge base '{KnowledgeBase}' retrieval failed (status {Status}); returning no results.",
                config.KnowledgeBaseName, ex.Status);
            return [];
        }

        var groundingText = ExtractGroundingText(response);
        if (groundingText is null)
        {
            _logger.LogDebug("Agentic retrieval returned no grounding content for the query.");
            return [];
        }

        var results = ParseGroundingPayload(groundingText, config.KnowledgeBaseName, topK, DateTimeOffset.UtcNow);
        _logger.LogDebug("Agentic retrieval returned {Count} result(s) for the query.", results.Count);
        return results;
    }

    /// <summary>
    /// Parses an Azure AI Search agentic-retrieval grounding payload (a JSON array of ranked
    /// chunks) into <see cref="RetrievalResult"/>s. Defensive against schema variation: field
    /// names are resolved with fallbacks, malformed or content-less entries are skipped, and a
    /// non-array or unparseable payload yields an empty list. Exposed <c>internal</c> for direct
    /// unit testing because this mapping — not the thin client round-trip — is where the risk is.
    /// </summary>
    /// <param name="payload">The JSON grounding payload from the knowledge base response.</param>
    /// <param name="knowledgeBaseName">Knowledge base name, used to synthesize a stable source URI.</param>
    /// <param name="topK">Maximum number of results to emit.</param>
    /// <param name="retrievedAt">Timestamp stamped as the chunk's <see cref="ChunkMetadata.CreatedAt"/>.</param>
    /// <returns>Results in the server-provided rank order, with positional proxy scores.</returns>
    internal static IReadOnlyList<RetrievalResult> ParseGroundingPayload(
        string payload,
        string knowledgeBaseName,
        int topK,
        DateTimeOffset retrievedAt)
    {
        if (string.IsNullOrWhiteSpace(payload) || topK <= 0)
            return [];

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            var results = new List<RetrievalResult>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (results.Count >= topK)
                    break;

                // results.Count is the OUTPUT rank: skipped (content-less) entries don't consume
                // a slot, so the top surviving chunk scores 1.0 rather than being demoted.
                var mapped = TryMapElement(element, results.Count, knowledgeBaseName, retrievedAt);
                if (mapped is not null)
                    results.Add(mapped);
            }

            return results;
        }
    }

    /// <summary>
    /// Maps a single grounding-array element to a <see cref="RetrievalResult"/>, or returns
    /// <see langword="null"/> when the element is not a content-bearing object (skipped).
    /// </summary>
    private static RetrievalResult? TryMapElement(
        JsonElement element,
        int rank,
        string knowledgeBaseName,
        DateTimeOffset retrievedAt)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        var content = GetString(element, "content", "text", "chunk");
        if (string.IsNullOrWhiteSpace(content))
            return null;

        var refId = GetString(element, "ref_id", "id", "doc_key") ?? $"kb-{rank}";
        var title = GetString(element, "title", "section_path", "sectionPath");

        // The service ranks results server-side, so list order is the relevance order. The
        // extractive payload carries no per-chunk numeric score, so encode the output rank as a
        // positional proxy: first = 1.0, decaying as 1/(1+rank).
        var score = 1.0 / (1 + rank);

        return new RetrievalResult
        {
            Chunk = BuildChunk(refId, title, content!, knowledgeBaseName, retrievedAt),
            DenseScore = score,
            SparseScore = 0.0,
            FusedScore = score
        };
    }

    /// <summary>
    /// Returns the first non-empty text payload from the knowledge base response — in extractive
    /// mode a single grounding message whose text content is the JSON array of ranked chunks.
    /// </summary>
    private static string? ExtractGroundingText(KnowledgeBaseRetrievalResponse response) =>
        response.Response
            .SelectMany(message => message.Content)
            .OfType<KnowledgeBaseMessageTextContent>()
            .Select(content => content.Text)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));

    private static DocumentChunk BuildChunk(
        string refId,
        string? title,
        string content,
        string knowledgeBaseName,
        DateTimeOffset retrievedAt) =>
        new()
        {
            Id = refId,
            DocumentId = refId,
            SectionPath = title ?? string.Empty,
            Content = content,
            // ~4 chars/token proxy — the knowledge base does not return an authoritative count.
            Tokens = Math.Max(1, content.Length / 4),
            Metadata = new ChunkMetadata
            {
                // Synthetic, stable identifier — the grounding payload carries no original
                // document URI. Fixed host on the reserved .invalid TLD (never resolves); the
                // knowledge base name and ref id go in the PATH, where Uri.EscapeDataString is
                // valid (escaping them into the HOST would throw for spaces/non-ASCII/long names).
                SourceUri = new Uri(
                    $"https://azure-knowledge-base.invalid/{Uri.EscapeDataString(knowledgeBaseName)}/{Uri.EscapeDataString(refId)}"),
                // Retrieval time — the knowledge base does not return original ingestion time.
                CreatedAt = retrievedAt,
                SiblingChunkIds = []
            },
            Embedding = null
        };

    private static string? GetString(JsonElement element, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (!element.TryGetProperty(name, out var value))
                continue;

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.ToString(),
                _ => null
            };
        }

        return null;
    }
}
