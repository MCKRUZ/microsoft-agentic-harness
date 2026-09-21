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
/// <b>Write gate still runs locally.</b> Unlike the local implementation, which resolves
/// <c>IMemoryWriteGate</c> from the same request scope, this class also runs it — first, before
/// ever contacting the remote service — rather than trusting the remote <c>/remember</c>
/// endpoint's own gate evaluation unverified. A local Reject never leaves this process. A local
/// Quarantine or Trusted result is still sent to the remote service, and the remote service's own
/// <see cref="RememberResponse.Trust"/> is combined conservatively (the more restrictive of the
/// two wins), so a compromised or misconfigured remote gate can only ever narrow trust, never
/// widen it.
/// </para>
/// <para>
/// <b>Caller identity is required, not defaulted.</b> An authenticated caller whose
/// <see cref="IKnowledgeScope.UserId"/> cannot be resolved is refused rather than written to or
/// read from a shared, unscoped bucket — proceeding unscoped is exactly the anti-pattern this
/// repo's own <c>Common Mistakes</c> section warns against for knowledge-scope handling.
/// <see cref="IKnowledgeScope.TenantId"/> is sent when present but not required, matching the
/// local backend's own <c>tenant ?? "default"</c> convention. Both requests also carry them so a
/// remote service that partitions its store by caller can honor per-caller isolation once its own
/// contract supports it — the current avatar contract does not filter on them yet, so true
/// cross-user recall isolation still depends on deployment topology (one harness+avatar pair per
/// isolation boundary) in the meantime; see the class's own tracking issue for the wire-contract
/// follow-up.
/// </para>
/// <para>
/// <see cref="ForgetAsync"/> throws <see cref="NotImplementedException"/>: the remote contract has
/// no per-fact delete endpoint, and the interface's one real caller
/// (<c>ForgetMemoryCommandHandler</c>, behind <c>DELETE /api/memory/{key}</c>) turns a bare
/// <see cref="Task"/> completion into an unconditional success response — silently claiming a
/// deletion that never happened would be a right-to-erasure and data-correctness defect, not a
/// harmless no-op. <see cref="ImproveAsync"/> remains a documented no-op: it is genuinely
/// fire-and-forget feedback with no correctness-sensitive caller, and the remote contract has no
/// matching endpoint.
/// </para>
/// </remarks>
public sealed class RemoteKnowledgeMemory : IKnowledgeMemory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IKnowledgeScope _scope;
    private readonly IMemoryWriteGate _writeGate;
    private readonly ILogger<RemoteKnowledgeMemory> _logger;

    /// <summary>Initializes a new instance of the <see cref="RemoteKnowledgeMemory"/> class.</summary>
    /// <param name="httpClientFactory">
    /// Resolves the <see cref="RemoteMemoryHttpClientNames.ClientName"/> client per call so the
    /// factory can pool and rotate the underlying handler.
    /// </param>
    /// <param name="scope">
    /// Supplies the ambient caller identity (<c>UserId</c>/<c>TenantId</c>) and conversation id
    /// used as the remote service's <c>threadId</c>.
    /// </param>
    /// <param name="writeGate">
    /// The same write gate the local backend uses, run before any content leaves this process.
    /// </param>
    /// <param name="logger">Logger for remember/recall diagnostics.</param>
    public RemoteKnowledgeMemory(
        IHttpClientFactory httpClientFactory,
        IKnowledgeScope scope,
        IMemoryWriteGate writeGate,
        ILogger<RemoteKnowledgeMemory> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(writeGate);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _scope = scope;
        _writeGate = writeGate;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<MemoryWriteDecision> RememberAsync(
        string key,
        string content,
        string entityType = "Fact",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_scope.UserId))
        {
            _logger.LogWarning(
                "Remote remember refused for key {Key}: no resolved caller identity — refusing to " +
                "write into a shared remote memory pool unscoped.", key);
            return new MemoryWriteDecision
            {
                Persist = false,
                Trust = MemoryTrust.Untrusted,
                Reason = "refused: no resolved caller identity",
            };
        }

        var localDecision = await _writeGate.EvaluateAsync(key, content, entityType, cancellationToken)
            .ConfigureAwait(false);
        if (!localDecision.Persist)
        {
            _logger.LogInformation(
                "Remote remember rejected locally for key {Key} before contacting the remote service: {Reason}",
                key, localDecision.Reason);
            return localDecision;
        }

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
                    UserId = _scope.UserId,
                    TenantId = _scope.TenantId,
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

            // Combine conservatively: the remote gate can only narrow what the local gate already
            // allowed, never widen it. A local Trusted + remote Untrusted stays Untrusted; a local
            // Trusted + remote Persist=false is not persisted at all.
            return new MemoryWriteDecision
            {
                Persist = result.Persist,
                Trust = result.Trust == MemoryTrust.Untrusted ? MemoryTrust.Untrusted : localDecision.Trust,
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
        if (string.IsNullOrWhiteSpace(_scope.UserId))
        {
            _logger.LogWarning(
                "Remote recall refused for query {Query}: no resolved caller identity.", query);
            return [];
        }

        try
        {
            using var client = _httpClientFactory.CreateClient(RemoteMemoryHttpClientNames.ClientName);
            using var response = await client.PostAsJsonAsync(
                "recall",
                new RecallRequest
                {
                    Query = query,
                    MaxResults = maxResults,
                    UserId = _scope.UserId,
                    TenantId = _scope.TenantId,
                },
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

            // The remote contract carries no per-result entity type, so entityType is accepted for
            // interface parity but not enforced here — every match is stamped with the same fixed,
            // honest type rather than fabricating agreement with the caller's filter. A caller that
            // needs the filter actually enforced must currently use the local backend.
            return recalled
                .Where(r => !string.IsNullOrWhiteSpace(r.Id) && !string.IsNullOrWhiteSpace(r.Content))
                .Select(r => new GraphNode
                {
                    Id = r.Id!,
                    Name = r.Id!,
                    Type = "Fact",
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
    /// <exception cref="NotImplementedException">
    /// Always thrown — the remote memory contract has no per-fact delete endpoint. See the class
    /// remarks for why this must fail loudly rather than report a silent, false success.
    /// </exception>
    public Task ForgetAsync(string key, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(
            "RemoteKnowledgeMemory.ForgetAsync has no remote endpoint to call — the avatar-hosting " +
            "memory contract does not yet support per-fact deletion. Reporting success here would " +
            "be a false right-to-erasure claim, so this fails loudly instead.");

    /// <inheritdoc />
    public Task ImproveAsync(
        string userMessage,
        string assistantResponse,
        IReadOnlyList<string> relevantNodeIds,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
