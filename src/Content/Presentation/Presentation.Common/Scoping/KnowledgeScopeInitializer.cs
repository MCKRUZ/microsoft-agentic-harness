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
    /// Establishes the knowledge scope from <paramref name="user"/>, reporting whether the caller may
    /// proceed at all.
    /// </summary>
    /// <param name="user">The principal to scope from, or <c>null</c>.</param>
    /// <param name="scopeWriter">The scope writer for this unit of work.</param>
    /// <param name="scopeToken">
    /// A restore token that reinstates the previously ambient scope when disposed. Dispose it when the
    /// unit of work ends: on an HTTP request that is merely tidy, but on a long-lived drain loop
    /// (a background job runner) it is what stops one job's identity bleeding into the next. Inert when
    /// no scope was established.
    /// </param>
    /// <returns>
    /// <c>true</c> when the caller may proceed — either scope was established, or the caller is
    /// <em>unauthenticated</em> and is intentionally left unscoped (anonymous endpoints, health probes),
    /// which the host's authorization policy is responsible for gating.
    /// <para>
    /// <c>false</c> when the principal AUTHENTICATED but carries no usable, unambiguous identity. The
    /// caller MUST reject the request rather than continue: proceeding would run authenticated work with
    /// no owner, and an unset owner is GLOBAL — the record becomes readable by every caller in every
    /// tenant. This is the one case where "carry on unscoped" degrades open, so it is not allowed.
    /// </para>
    /// </returns>
    /// <remarks>
    /// The authenticated/unauthenticated split is the whole design. A caller who never presented
    /// credentials has not asked to own anything, so leaving scope unset is honest and authorization
    /// still guards the endpoint. A caller who DID authenticate but whose identity cannot be resolved —
    /// no <c>oid</c>/<c>sub</c>, or two conflicting values — is asking to act as somebody, and we cannot
    /// say who. Letting that through is how an unscoped write becomes a world-readable record.
    /// </remarks>
    public static bool TryApply(
        ClaimsPrincipal? user, IKnowledgeScopeWriter scopeWriter, out IDisposable scopeToken)
    {
        ArgumentNullException.ThrowIfNull(scopeWriter);

        scopeToken = NoOpScopeToken.Instance;

        if (user?.Identity?.IsAuthenticated != true)
            return true;

        var userId = user.GetUserIdOrNull();
        if (userId is null)
            return false;

        // Set only user + tenant: that is what memory namespacing keys on. Dataset properties are
        // left unset so they keep falling back to the configured defaults; dataset-level scope is a
        // later concern (the graph-store isolation decorators), not part of conversation-memory scope.
        scopeToken = scopeWriter.SetScope(userId: userId, tenantId: user.GetTenantId());
        return true;
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
