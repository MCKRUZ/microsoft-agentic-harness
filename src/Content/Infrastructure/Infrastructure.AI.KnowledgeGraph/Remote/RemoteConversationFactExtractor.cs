using System.Net.Http.Json;
using System.Text.Json;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.KnowledgeGraph.Remote;

/// <summary>
/// Extracts conversation facts by delegating to an external memory-hosting service's
/// <c>POST {avatarId}/extract</c> endpoint, instead of running this harness's own local
/// LLM-based extraction. Registered in place of <c>ConversationFactExtractor</c> when
/// <c>AppConfig:AI:RemoteMemory:Enabled</c> is <see langword="true"/>.
/// </summary>
/// <remarks>
/// Follows the same fail-safe contract as the interface's local implementation: every expected
/// failure (HTTP error, malformed response, cancellation aside) is caught and logged, never
/// thrown, so a remote outage degrades to "no facts extracted this turn" rather than breaking the
/// agent turn that triggered it.
/// </remarks>
public sealed class RemoteConversationFactExtractor : IConversationFactExtractor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RemoteConversationFactExtractor> _logger;

    /// <summary>Initializes a new instance of the <see cref="RemoteConversationFactExtractor"/> class.</summary>
    /// <param name="httpClientFactory">
    /// Resolves the <see cref="RemoteMemoryHttpClientNames.ClientName"/> client per call so the
    /// factory can pool and rotate the underlying handler.
    /// </param>
    /// <param name="logger">Logger for extraction diagnostics.</param>
    public RemoteConversationFactExtractor(
        IHttpClientFactory httpClientFactory,
        ILogger<RemoteConversationFactExtractor> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConversationFact>> ExtractAsync(
        string userMessage,
        string assistantResponse,
        string conversationId,
        int turnNumber,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient(RemoteMemoryHttpClientNames.ClientName);
            using var response = await client.PostAsJsonAsync(
                "extract",
                new ExtractFactsRequest
                {
                    ThreadId = conversationId,
                    UserMessage = userMessage,
                    AssistantResponse = assistantResponse,
                    TurnNumber = turnNumber,
                    // Avatar uses this purely for its own idempotency/tracing; this harness's
                    // IConversationFactExtractor contract carries no run identifier, so a fresh
                    // per-call id satisfies that without widening the interface.
                    RunId = Guid.NewGuid().ToString("N"),
                },
                JsonOptions,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Remote fact extraction returned HTTP {StatusCode} for conversation {ConversationId} turn {Turn}; returning no facts.",
                    (int)response.StatusCode, conversationId, turnNumber);
                return [];
            }

            var extracted = await response.Content
                .ReadFromJsonAsync<List<ExtractedFactResponse>>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (extracted is null || extracted.Count == 0)
                return [];

            var facts = extracted
                .Where(f => !string.IsNullOrWhiteSpace(f.Key) && !string.IsNullOrWhiteSpace(f.Content))
                .Select(f => new ConversationFact
                {
                    Key = f.Key!,
                    Content = f.Content!,
                    EntityType = f.EntityType,
                    Confidence = f.Confidence,
                })
                .ToList();

            _logger.LogDebug(
                "Remote extraction returned {Count} fact(s) for conversation {ConversationId} turn {Turn}",
                facts.Count, conversationId, turnNumber);

            return facts;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // A client-side timeout also throws OperationCanceledException, so the caller's own
            // token — not the exception type — is what distinguishes "the caller cancelled" from
            // "the remote call failed"; see the analogous fix in MultiSourceOrchestrator.
            _logger.LogWarning(ex,
                "Remote fact extraction failed for conversation {ConversationId} turn {Turn}; returning no facts.",
                conversationId, turnNumber);
            return [];
        }
    }
}
