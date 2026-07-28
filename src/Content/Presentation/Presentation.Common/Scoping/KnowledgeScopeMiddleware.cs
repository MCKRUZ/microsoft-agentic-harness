using Application.AI.Common.Interfaces.KnowledgeGraph;
using Microsoft.AspNetCore.Http;

namespace Presentation.Common.Scoping;

/// <summary>
/// Establishes the per-request knowledge scope (user + tenant) from the authenticated
/// <see cref="HttpContext.User"/> for every HTTP request. This is the chokepoint that lets
/// cross-session memory, graph-store isolation, and plan-state ownership attribute work to the
/// correct user/tenant, covering controllers and minimal-API endpoints alike.
/// </summary>
/// <remarks>
/// <para>
/// Must run <em>after</em> <c>UseAuthentication</c> so <see cref="HttpContext.User"/> is populated.
/// <see cref="IKnowledgeScopeWriter"/> is resolved per request (method injection), so the scope is
/// set on the same scoped instance the downstream MediatR handler and graph-store decorators read.
/// </para>
/// <para>
/// Every host that can reach a scope-stamped store must mount this. A host that does not leaves the
/// ambient scope null, and a null owner is treated as <em>global</em> by the persistence scope filters
/// (<c>PlannerScopeFilter.VisibleTo</c>, <c>TenantIsolatedGraphStore</c>) — so the records it writes
/// become readable by every caller in every tenant instead of private to their author.
/// </para>
/// <para>
/// Unauthenticated requests (anonymous endpoints, health probes, a dev auth bypass without an
/// <c>oid</c>) leave the scope at its configured default — the writer is only invoked when a user id
/// is present. Keeping unauthenticated callers away from scope-stamped stores is the job of the host's
/// authentication policy, not of this middleware.
/// </para>
/// </remarks>
public sealed class KnowledgeScopeMiddleware
{
    private readonly RequestDelegate _next;

    /// <summary>
    /// Initializes a new instance of the <see cref="KnowledgeScopeMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    public KnowledgeScopeMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        _next = next;
    }

    /// <summary>
    /// Sets the knowledge scope from the authenticated principal, invokes the rest of the pipeline,
    /// then restores the previously ambient scope.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="scopeWriter">The request-scoped knowledge scope writer.</param>
    public async Task InvokeAsync(HttpContext context, IKnowledgeScopeWriter scopeWriter)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Disposing on the way out restores the previous ambient value. Post-turn background writes
        // started during the request captured their execution context while the scope was still set,
        // so they keep the caller's identity — the restore only affects this flow.
        using var scopeToken = KnowledgeScopeInitializer.Apply(context.User, scopeWriter);
        await _next(context);
    }
}
