using System.Security.Claims;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Presentation.Common.Extensions;

namespace Presentation.Common.Scoping;

/// <summary>
/// Maps an authenticated <see cref="ClaimsPrincipal"/> onto the request's
/// <see cref="IKnowledgeScopeWriter"/>. Shared by the HTTP <see cref="KnowledgeScopeMiddleware"/>
/// and the SignalR <c>KnowledgeScopeHubFilter</c> so both transports establish scope identically.
/// </summary>
/// <remarks>
/// Lives in <c>Presentation.Common</c> rather than in one host: <em>every</em> host that can reach a
/// scope-stamped store (plans, knowledge graph, cross-session memory) has to establish identity, and a
/// host that forgets leaves records stamped with a null owner — which reads as <em>global</em>
/// (visible to every caller), not as "private".
/// </remarks>
public static class KnowledgeScopeInitializer
{
    /// <summary>
    /// Sets the knowledge scope from <paramref name="user"/> when a user id is present; otherwise
    /// leaves scope at its configured default (anonymous / health probes / dev auth without an oid).
    /// </summary>
    /// <param name="user">The authenticated principal, or <c>null</c>.</param>
    /// <param name="scopeWriter">The request-scoped scope writer.</param>
    /// <returns>
    /// A restore token that reinstates the previously ambient scope when disposed. Dispose it when the
    /// unit of work ends: on an HTTP request that is merely tidy, but on a long-lived drain loop
    /// (a background job runner) it is what stops one job's identity bleeding into the next. When no
    /// user id is present the returned token is inert.
    /// </returns>
    public static IDisposable Apply(ClaimsPrincipal? user, IKnowledgeScopeWriter scopeWriter)
    {
        ArgumentNullException.ThrowIfNull(scopeWriter);

        var userId = user?.GetUserIdOrNull();
        if (userId is null)
            return NoOpScopeToken.Instance;

        // Set only user + tenant: that is what memory namespacing keys on. Dataset properties are
        // left unset so they keep falling back to the configured defaults; dataset-level scope is a
        // later concern (the graph-store isolation decorators), not part of conversation-memory scope.
        return scopeWriter.SetScope(userId: userId, tenantId: user!.GetTenantId());
    }

    /// <summary>Inert token returned when no scope was established, so callers can always <c>using</c>.</summary>
    private sealed class NoOpScopeToken : IDisposable
    {
        internal static readonly NoOpScopeToken Instance = new();

        public void Dispose()
        {
            // Nothing was set, so there is nothing to restore.
        }
    }
}
