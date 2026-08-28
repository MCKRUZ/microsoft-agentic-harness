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
    private static readonly AsyncLocal<ConcurrentDictionary<string, IList<AITool>>?> s_current = new();

    /// <summary>The active cache for the current async flow, or <see langword="null"/> when no scope is open.</summary>
    public static ConcurrentDictionary<string, IList<AITool>>? Current => s_current.Value;

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
        s_current.Value = new ConcurrentDictionary<string, IList<AITool>>();
        return new CacheScope(previous);
    }

    private sealed class CacheScope(ConcurrentDictionary<string, IList<AITool>>? previous) : IDisposable
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
