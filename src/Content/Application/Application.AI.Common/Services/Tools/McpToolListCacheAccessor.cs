using System.Collections.Concurrent;
using Microsoft.Extensions.AI;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// Ambient, per-flow cache of each MCP server's tool list, so a caller who has already fetched
/// every connected server's tools (<see cref="Application.AI.Common.Interfaces.IMcpToolProvider.GetAllToolsAsync"/>)
/// does not pay for a second wire round trip when something later in the same logical request
/// re-resolves individual servers by name (<see cref="Application.AI.Common.Interfaces.IMcpToolProvider.GetToolsAsync"/>).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why ambient, not DI-scoped.</strong> <c>IMcpToolProvider</c> and <c>IDirectToolInvoker</c> are
/// both registered as singletons — a caller resolves the exact same provider instance on every request,
/// so a Scoped-lifetime decorator could not compose here even if the container allowed it (a singleton
/// consumer capturing a scoped dependency at construction is a captive dependency, and the container
/// would refuse to build). Follows the same pattern as this codebase's other per-flow accessors
/// (<c>CapabilityEnvelopeAccessor</c>, <c>ToolAdmissionAccessor</c>): an <see cref="AsyncLocal{T}"/> the
/// request path sets at the start of the work it wants cached and clears in a <c>finally</c>, read at the
/// provider. When unset — every call outside a begun scope — the provider sees no cache and behaves
/// exactly as it did before this existed, so this adds zero behaviour off the one call site that opens it.
/// </para>
/// <para>
/// <strong>One direction only, deliberately.</strong> Only a <c>GetAllToolsAsync</c>-equivalent fetch
/// publishes into the cache; a single <c>GetToolsAsync(serverName)</c> call only ever reads it — that
/// asymmetry is the point, since populating from a single-server call would not capture the other servers
/// a later per-server lookup might need. The scenario this exists for — <c>McpController.ResolveMcpEnvelopeAsync</c>'s
/// ungranted-caller fallback fetching every server's tools to build a wide-open envelope, then
/// <c>DirectToolInvoker.ResolveGrantedMcpToolAsync</c> re-fetching each of those same servers one at a
/// time to find the one tool being invoked — is exactly this shape: the first call's discovered servers
/// are a superset of the second call's per-server lookups within the same request.
/// </para>
/// <para>
/// <strong>Read/write access is behind methods, not a raw dictionary.</strong> An earlier version
/// exposed the underlying <see cref="ConcurrentDictionary{TKey,TValue}"/> directly through <c>Current</c>
/// — a public, directly-mutable collection, which this repo's own coding-style rule requires public
/// surfaces to avoid (<c>IReadOnlyDictionary&lt;K,V&gt;</c> or narrower). Only <c>CachingMcpToolProvider</c>
/// is meant to write, so the write surface is <see cref="TrySet"/>/<see cref="TryAdd"/> rather than a
/// settable collection any holder of a reference could mutate (#495 security review, finding L3/#3).
/// </para>
/// <para>
/// <strong>The backing dictionary is allocated lazily, on first write.</strong> <see cref="Begin"/> is
/// called on every MCP tool invocation, including the common case — a caller with an operator-configured
/// grant — where nothing is ever cached because <c>ResolveMcpEnvelopeAsync</c> never takes the
/// <c>GetAllToolsAsync</c> fallback branch that would populate it. Eagerly allocating a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> on every call would pay that cost on the majority path
/// for a benefit only the minority fallback path uses (#495 review finding #8); deferring the allocation
/// to the first actual write means an unused scope costs one small wrapper object, not a dictionary.
/// </para>
/// <para>
/// Caveat, identical to the other ambient accessors: like any <see cref="AsyncLocal{T}"/>, the cache is
/// captured into the <c>ExecutionContext</c> of any fire-and-forget work started <em>while it is
/// active</em>, and that work keeps reading it after the request's own flow has torn it down. The one
/// call site's scope spans the whole tool invocation, not just list resolution — it has to, since the
/// second per-server fetch happens inside <c>InvokeMcpToolAsync</c>'s own preflight — so anything the
/// invoked tool transitively triggers while that scope is open is served from this cache too.
/// </para>
/// </remarks>
public static class McpToolListCacheAccessor
{
    private static readonly AsyncLocal<Scope?> s_current = new();

    /// <summary>
    /// Whether a cache scope is open for the current async flow. Read-only, unlike the retired
    /// <c>Current</c> property this type used to expose — lets a caller behave identically to an
    /// undecorated provider off the one call site that opens a scope, without handing out anything
    /// mutable to check that against.
    /// </summary>
    public static bool IsActive => s_current.Value is not null;

    /// <summary>
    /// Attempts to read a previously-cached server's tool list for the current async flow.
    /// </summary>
    /// <returns><see langword="true"/> when a scope is open and it holds an entry for <paramref name="serverName"/>.</returns>
    public static bool TryGet(string serverName, out IList<AITool> tools)
    {
        var cache = s_current.Value?.Cache;
        if (cache is not null && cache.TryGetValue(serverName, out var found))
        {
            tools = found;
            return true;
        }

        tools = null!;
        return false;
    }

    /// <summary>
    /// Caches <paramref name="tools"/> for <paramref name="serverName"/> only if no entry already exists,
    /// for the rest of the current scope. A no-op when no scope is open.
    /// </summary>
    /// <remarks>
    /// Used by an opportunistic single-server fetch (<c>IMcpToolProvider.GetToolsAsync</c>) — "only if
    /// absent" so a single-server lookup can never clobber a fresher, authoritative entry a preceding
    /// <c>GetAllToolsAsync</c> discovery already published for the same server.
    /// </remarks>
    public static void TryAdd(string serverName, IList<AITool> tools)
    {
        var scope = s_current.Value;
        if (scope is null)
            return;

        GetOrCreateCache(scope).TryAdd(serverName, tools);
    }

    /// <summary>
    /// Caches <paramref name="tools"/> for <paramref name="serverName"/>, unconditionally overwriting any
    /// existing entry, for the rest of the current scope. A no-op when no scope is open.
    /// </summary>
    /// <remarks>
    /// Used by an authoritative discovery fetch (<c>IMcpToolProvider.GetAllToolsAsync</c>), which always
    /// overwrites — a re-discovery within the same scope is the freshest information available and
    /// should win over whatever a stale entry held.
    /// </remarks>
    public static void TrySet(string serverName, IList<AITool> tools)
    {
        var scope = s_current.Value;
        if (scope is null)
            return;

        GetOrCreateCache(scope)[serverName] = tools;
    }

    private static ConcurrentDictionary<string, IList<AITool>> GetOrCreateCache(Scope scope) =>
        scope.Cache ??= new ConcurrentDictionary<string, IList<AITool>>();

    /// <summary>
    /// Opens an empty cache for the current async flow and returns a handle that restores the previous
    /// ambient value when disposed. Use with <c>using</c> so the cache is torn down for the request's own
    /// flow when it completes, even on exception — a cache that outlived its request would go stale the
    /// moment any MCP server's tool list changed. See this type's remarks for the one exception: work
    /// started detached while the scope is open keeps its own captured reference.
    /// </summary>
    public static IDisposable Begin()
    {
        var previous = s_current.Value;
        s_current.Value = new Scope();
        return new CacheScope(previous);
    }

    /// <summary>The per-flow state: a lazily-allocated backing dictionary, present only once something is cached.</summary>
    private sealed class Scope
    {
        public ConcurrentDictionary<string, IList<AITool>>? Cache;
    }

    private sealed class CacheScope(Scope? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            s_current.Value = previous;
        }
    }
}
