using System.Net.Http.Json;
using System.Text.Json;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.KnowledgeGraph.Remote;

/// <summary>
/// Answers this harness's Remember/Recall/Forget/Improve memory seam from an external
/// memory-hosting service's <c>{avatarId}/remember</c> and <c>{avatarId}/recall</c> endpoints,
/// instead of this harness's own local session cache + graph store. Registered in place of
/// <c>KnowledgeMemoryService</c> when <c>AppConfig:AI:RemoteMemory:Enabled</c> is
/// <see langword="true"/>.
/// </summary>
/// <remarks>
/// <para>
/// Unlike the local implementation, <see cref="RememberAsync"/> does not also call
/// <c>IMemoryWriteGate</c> — the remote service's own <c>/remember</c> endpoint already runs its
/// write-gate evaluation server-side and returns the outcome directly.
/// </para>
/// <para>
/// <see cref="ForgetAsync"/> and <see cref="ImproveAsync"/> are documented no-ops: the remote
/// contract exposes only extract/remember/recall/prune/cross-session endpoints, none of which
/// map to a per-fact forget or a feedback-driven weight update. Callers already treat both
/// methods as fire-and-forget (see the interface's own remarks), so a no-op is a safe, honest
/// implementation rather than a fabricated call to an endpoint that does not exist.
/// </para>
/// </remarks>
public sealed class RemoteKnowledgeMemory : IKnowledgeMemory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IKnowledgeScope _scope;
    private readonly ILogger<RemoteKnowledgeMemory> _logger;

    /// <summary>Initializes a new instance of the <see cref="RemoteKnowledgeMemory"/> class.</summary>
    /// <param name="httpClientFactory">
    /// Resolves the <see cref="RemoteMemoryHttpClientNames.ClientName"/> client per call so the
    /// factory can pool and rotate the underlying handler.
    /// </param>
    /// <param name="scope">
    /// Supplies the ambient conversation id used as the remote service's <c>threadId</c>.
    /// </param>
    /// <param name="logger">Logger for remember/recall diagnostics.</param>
    public RemoteKnowledgeMemory(
        IHttpClientFactory httpClientFactory,
        IKnowledgeScope scope,
        ILogger<RemoteKnowledgeMemory> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _scope = scope;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<MemoryWriteDecision> RememberAsync(
        string key,
        string content,
        string entityType = "Fact",
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient(RemoteMemoryHttpClientNames.ClientName);
            using var response = await client.PostAsJsonAsync(
                "remember",
                new RememberRequest
                {
                    ThreadId = _scope.ConversationId ?? "unscoped",
                    Key = key,
                    Content = content,
                    EntityType = entityType,
                },
                JsonOptions,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Remote remember returned HTTP {StatusCode} for key {Key}; treating as rejected.",
                    (int)response.StatusCode, key);
                return new MemoryWriteDecision
                {
                    Persist = false,
                    Trust = MemoryTrust.Untrusted,
                    Reason = $"remote remember failed: HTTP {(int)response.StatusCode}",
                };
            }

            var result = await response.Content
                .ReadFromJsonAsync<RememberResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (result is null)
            {
                _logger.LogWarning("Remote remember returned an empty body for key {Key}; treating as rejected.", key);
                return new MemoryWriteDecision
                {
                    Persist = false,
                    Trust = MemoryTrust.Untrusted,
                    Reason = "remote remember returned no body",
                };
            }

            return new MemoryWriteDecision
            {
                Persist = result.Persist,
                Trust = result.Trust,
                Reason = result.Reason,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Remote remember failed for key {Key}; treating as rejected.", key);
            return new MemoryWriteDecision
            {
                Persist = false,
                Trust = MemoryTrust.Untrusted,
                Reason = "remote remember threw",
            };
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphNode>> RecallAsync(
        string query,
        int maxResults = 5,
        string? entityType = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient(RemoteMemoryHttpClientNames.ClientName);
            using var response = await client.PostAsJsonAsync(
                "recall",
                new RecallRequest { Query = query, MaxResults = maxResults },
                JsonOptions,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Remote recall returned HTTP {StatusCode} for query {Query}; returning no matches.",
                    (int)response.StatusCode, query);
                return [];
            }

            var recalled = await response.Content
                .ReadFromJsonAsync<List<RecalledMemoryResponse>>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            if (recalled is null || recalled.Count == 0)
                return [];

            return recalled
                .Where(r => !string.IsNullOrWhiteSpace(r.Id) && !string.IsNullOrWhiteSpace(r.Content))
                .Select(r => new GraphNode
                {
                    Id = r.Id!,
                    Name = r.Id!,
                    Type = entityType ?? "Fact",
                    Properties = new Dictionary<string, string> { ["content"] = r.Content! },
                })
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Remote recall failed for query {Query}; returning no matches.", query);
            return [];
        }
    }

    /// <inheritdoc />
    public Task ForgetAsync(string key, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task ImproveAsync(
        string userMessage,
        string assistantResponse,
        IReadOnlyList<string> relevantNodeIds,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
