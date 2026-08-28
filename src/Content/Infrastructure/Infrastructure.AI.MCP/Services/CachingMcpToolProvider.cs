using System.Collections.ObjectModel;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Services.Tools;
using Microsoft.Extensions.AI;

namespace Infrastructure.AI.MCP.Services;

/// <summary>
/// Decorates an <see cref="IMcpToolProvider"/> so a caller who already fetched every connected server's
/// tools within the current request does not pay for a second per-server round trip when something later
/// in that same request re-resolves individual servers by name (#495).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Outermost in the decorator chain, deliberately.</strong> A cache hit here must skip everything
/// downstream — the wire call, the security scan, and re-recording identical tool-behaviour annotations
/// into <c>IToolBehaviorRegistry</c> a second time for data that was already recorded on the first,
/// real fetch — not just the wire. Wrapping <c>BehaviorRecordingMcpToolProvider</c> is what makes that
/// true; wrapping anything further in would leave the redundant work this exists to remove still running
/// on a cache hit.
/// </para>
/// <para>
/// Reads and writes go through <see cref="McpToolListCacheAccessor"/>, which is <see langword="null"/>
/// outside a request that has explicitly opened a cache scope — see its own remarks for why an ambient
/// per-flow value is used here rather than a DI-scoped registration. Off that one call site, every method
/// here behaves exactly as the provider it decorates.
/// </para>
/// </remarks>
public sealed class CachingMcpToolProvider : IMcpToolProvider
{
    private readonly IMcpToolProvider _inner;

    /// <summary>Initializes the decorator.</summary>
    /// <param name="inner">The tool provider whose results are cached.</param>
    public CachingMcpToolProvider(IMcpToolProvider inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A list served from the cache is shared with every other caller who asks for the same server
    /// within this request, so it is wrapped read-only before caching (#495 security review, finding
    /// L3) — a formerly-private, freshly-allocated list becoming a value multiple callers alias would
    /// otherwise let one consumer's in-place mutation silently corrupt what the grant-enforcement
    /// re-resolution (<c>DirectToolInvoker.ResolveGrantedMcpToolAsync</c>) reads. No current consumer
    /// mutates the list it gets back, but the invariant is made structural rather than conventional.
    /// </remarks>
    public async Task<IList<AITool>> GetToolsAsync(string serverName, CancellationToken cancellationToken = default)
    {
        if (McpToolListCacheAccessor.TryGet(serverName, out var cached))
            return cached;

        var tools = await _inner.GetToolsAsync(serverName, cancellationToken).ConfigureAwait(false);
        if (!McpToolListCacheAccessor.IsActive)
            return tools;

        var snapshot = Snapshot(tools);
        McpToolListCacheAccessor.TryAdd(serverName, snapshot);
        return snapshot;
    }

    /// <inheritdoc />
    /// <remarks>See <see cref="GetToolsAsync"/>'s remarks — the same read-only-snapshot reasoning applies here.</remarks>
    public async Task<Dictionary<string, IList<AITool>>> GetAllToolsAsync(CancellationToken cancellationToken = default)
    {
        var discovered = await _inner.GetAllToolsAsync(cancellationToken).ConfigureAwait(false);

        if (!McpToolListCacheAccessor.IsActive)
            return discovered;

        // Mutated in place rather than copied into a second dictionary: discovered is a fresh instance
        // this call exclusively owns (ScanningMcpToolProvider.GetAllToolsAsync allocates it and hands
        // it nowhere else), so there is no other holder for a second dictionary to protect against.
        foreach (var serverName in discovered.Keys)
        {
            var readOnly = Snapshot(discovered[serverName]);
            discovered[serverName] = readOnly;
            McpToolListCacheAccessor.TrySet(serverName, readOnly);
        }

        return discovered;
    }

    /// <summary>
    /// Wraps <paramref name="tools"/> read-only without copying it — safe because every producer in the
    /// chain (<c>ScanningMcpToolProvider.Screen</c>, and <c>BehaviorRecordingMcpToolProvider</c>'s
    /// pass-through) hands back a freshly-allocated list this call exclusively owns, never one retained
    /// or reused elsewhere. An earlier version defensively re-copied it (<c>[.. tools]</c>) before
    /// wrapping; <c>/simplify</c>'s reuse and efficiency passes both flagged that as a wasted allocation
    /// once the ownership was traced end to end.
    /// </summary>
    private static ReadOnlyCollection<AITool> Snapshot(IList<AITool> tools) => new(tools);

    /// <inheritdoc />
    /// <remarks>
    /// Not cached. This searches every configured server for one tool by name — a different shape from
    /// the "fetch this server's full list" call the cache exists for, and
    /// <c>BehaviorRecordingMcpToolProvider</c>'s own by-name lookup is left unrecorded for the analogous
    /// reason (see its remarks): this path does not report which server answered, so there is nothing to
    /// key a per-server cache entry on.
    /// </remarks>
    public Task<AIFunction?> GetToolByNameAsync(string name, CancellationToken cancellationToken = default) =>
        _inner.GetToolByNameAsync(name, cancellationToken);

    /// <inheritdoc />
    public Task<bool> IsServerAvailableAsync(string serverName, CancellationToken cancellationToken = default) =>
        _inner.IsServerAvailableAsync(serverName, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Does not dispose the decorated provider — the container registers both as singletons and owns
    /// its lifetime, matching the arrangement the scanning and behavior-recording decorators have with
    /// the transport provider.
    /// </remarks>
    public void Dispose()
    {
    }
}
