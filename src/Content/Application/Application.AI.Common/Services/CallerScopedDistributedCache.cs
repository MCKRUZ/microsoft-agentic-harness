using System.Security.Cryptography;
using System.Text;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Scoping;
using Domain.Common.Compliance;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace Application.AI.Common.Services;

/// <summary>
/// Wraps an <see cref="IDistributedCache"/> so every entry belongs to the caller who wrote it: entries are filed under
/// the current caller's tenant and user, and nothing is read or written when the caller is unknown.
/// </summary>
/// <remarks>
/// <para>
/// Used for the chat response cache. Microsoft.Extensions.AI's <c>DistributedCachingChatClient</c> builds its key from
/// the conversation and the chat options alone, so two callers who send the same conversation to the same agent would
/// share one entry: a second user, or a user in another tenant, would get the first caller's stored answer without the
/// model being called. The hit is observable (milliseconds against seconds), which tells any caller whether someone
/// else has already asked a given question, and it carries a store the retention work treats as personal data across
/// the tenant boundary. Scoping at the storage layer, rather than in a subclass of the caching client, leaves the place
/// that turns the cache on unchanged and visible to the personal-data inventory.
/// </para>
/// <para>
/// The caller is read per operation from <see cref="IKnowledgeScope"/> in the ambient request scope, never captured at
/// construction: agents, and the chat clients inside them, are cached and shared across requests. An operation with no
/// ambient request scope, no knowledge scope, or no user is unattributed, and is not cached at all: a read misses and a
/// write is dropped. Hosts with no signed-in caller (the console, the eval runner) therefore get no response cache.
/// </para>
/// <para>
/// The tenant and user, canonicalized as the knowledge graph's stores canonicalize them, are hashed with HMAC-SHA256
/// under a random key that exists only in this process. A plain hash of a guessable id would let anyone who can read a
/// shared Redis cache confirm whose entries are whose; this key cannot be recomputed outside the process. The cost is
/// that hosts sharing one Redis cache do not share hits, and entries written before a restart are no longer reachable
/// and simply expire.
/// </para>
/// </remarks>
[PersonalDataChannel("Files chat response cache entries under the writing caller; the chat response cache (AgentFactory) describes what it holds.")]
public sealed class CallerScopedDistributedCache : IDistributedCache
{
    /// <summary>
    /// The key callers are hashed under: random, held only in this process's memory, and never stored. Nobody who can
    /// read the shared cache can recompute which entries belong to a given person.
    /// </summary>
    private static readonly byte[] ProcessKey = RandomNumberGenerator.GetBytes(32);

    private readonly IDistributedCache _inner;
    private readonly IAmbientRequestScope _ambientScope;

    /// <summary>Initializes a new instance of the <see cref="CallerScopedDistributedCache"/> class.</summary>
    /// <param name="inner">The cache entries are stored in.</param>
    /// <param name="ambientScope">The request scope the current caller's <see cref="IKnowledgeScope"/> is read from.</param>
    public CallerScopedDistributedCache(IDistributedCache inner, IAmbientRequestScope ambientScope)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(ambientScope);

        _inner = inner;
        _ambientScope = ambientScope;
    }

    /// <inheritdoc />
    public byte[]? Get(string key) => ScopedKey(key) is { } scoped ? _inner.Get(scoped) : null;

    /// <inheritdoc />
    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) =>
        ScopedKey(key) is { } scoped ? _inner.GetAsync(scoped, token) : Task.FromResult<byte[]?>(null);

    /// <inheritdoc />
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        if (ScopedKey(key) is { } scoped)
            _inner.Set(scoped, value, options);
    }

    /// <inheritdoc />
    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) =>
        ScopedKey(key) is { } scoped ? _inner.SetAsync(scoped, value, options, token) : Task.CompletedTask;

    /// <inheritdoc />
    public void Refresh(string key)
    {
        if (ScopedKey(key) is { } scoped)
            _inner.Refresh(scoped);
    }

    /// <inheritdoc />
    public Task RefreshAsync(string key, CancellationToken token = default) =>
        ScopedKey(key) is { } scoped ? _inner.RefreshAsync(scoped, token) : Task.CompletedTask;

    /// <inheritdoc />
    public void Remove(string key)
    {
        if (ScopedKey(key) is { } scoped)
            _inner.Remove(scoped);
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken token = default) =>
        ScopedKey(key) is { } scoped ? _inner.RemoveAsync(scoped, token) : Task.CompletedTask;

    /// <summary>
    /// <paramref name="key"/> filed under the current caller, or <see langword="null"/> when the caller is unknown and
    /// the operation must not touch the cache.
    /// </summary>
    private string? ScopedKey(string key)
    {
        IKnowledgeScope? caller;
        try
        {
            caller = _ambientScope.Current?.GetService<IKnowledgeScope>();
        }
        catch (ObjectDisposedException)
        {
            // Work that outlived its request scope has no caller left to file under; a cache must not fail the turn.
            return null;
        }

        var user = ScopeIdentity.Canonicalize(caller?.UserId);
        if (user is null)
            return null;

        // Each part is length-prefixed, so no pair of ids, whatever characters they hold, encodes like another pair.
        var tenant = ScopeIdentity.Canonicalize(caller!.TenantId) ?? string.Empty;
        var identity = Encoding.UTF8.GetBytes($"{tenant.Length}:{tenant}{user.Length}:{user}");
        return $"{Convert.ToHexStringLower(HMACSHA256.HashData(ProcessKey, identity))}:{key}";
    }
}
