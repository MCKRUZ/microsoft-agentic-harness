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
    public async Task<IList<AITool>> GetToolsAsync(string serverName, CancellationToken cancellationToken = default)
    {
        var cache = McpToolListCacheAccessor.Current;
        if (cache is not null && cache.TryGetValue(serverName, out var cached))
            return cached;

        var tools = await _inner.GetToolsAsync(serverName, cancellationToken).ConfigureAwait(false);
        cache?.TryAdd(serverName, tools);
        return tools;
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, IList<AITool>>> GetAllToolsAsync(CancellationToken cancellationToken = default)
    {
        var discovered = await _inner.GetAllToolsAsync(cancellationToken).ConfigureAwait(false);

        var cache = McpToolListCacheAccessor.Current;
        if (cache is not null)
        {
            foreach (var (serverName, tools) in discovered)
                cache[serverName] = tools;
        }

        return discovered;
    }

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
