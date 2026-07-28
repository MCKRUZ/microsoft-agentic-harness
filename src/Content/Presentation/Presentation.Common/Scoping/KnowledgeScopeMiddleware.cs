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
/// <b>Unauthenticated</b> requests (anonymous endpoints, health probes) proceed with the scope left at
/// its configured default. A caller who presented no credentials has not asked to own anything, and
/// keeping them away from scope-stamped stores is the host's authorization policy's job, not this
/// middleware's.
/// </para>
/// <para>
/// <b>Authenticated requests whose identity cannot be resolved are REJECTED</b> with 401 — no
/// <c>oid</c>/<c>sub</c> in any accepted form, or two conflicting values. Such a caller is asking to act
/// as somebody and we cannot say who; letting them proceed unscoped would write an unowned record, and
/// unowned means GLOBAL. This is the middleware's fail-closed edge: every other route by which
/// "identity resolved to nothing" could reach persistence has been closed one at a time, and this is
/// the chokepoint where the remaining ones die.
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
    /// then restores the previously ambient scope. Rejects a request whose principal authenticated but
    /// carries no usable identity.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="scopeWriter">The request-scoped knowledge scope writer.</param>
    public async Task InvokeAsync(HttpContext context, IKnowledgeScopeWriter scopeWriter)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!KnowledgeScopeInitializer.TryApply(context.User, scopeWriter, out var scopeToken))
        {
            await WriteUnusableIdentityAsync(context);
            return;
        }

        // Disposing on the way out restores the previous ambient value. Post-turn background writes
        // started during the request captured their execution context while the scope was still set,
        // so they keep the caller's identity — the restore only affects this flow.
        using (scopeToken)
        {
            await _next(context);
        }
    }

    /// <summary>
    /// Short-circuits with 401 when an authenticated principal has no usable identity. Matches the status
    /// <c>BundlesController</c> already returns for the same condition, and the response body shape of
    /// <c>GlobalExceptionMiddleware</c>. Deliberately says nothing about which claim was missing or
    /// ambiguous — that would tell a caller probing with injected claims what to try next.
    /// </summary>
    private static Task WriteUnusableIdentityAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";

        return context.Response.WriteAsJsonAsync(new
        {
            error = "The authenticated principal carries no usable identity.",
            statusCode = StatusCodes.Status401Unauthorized,
            timestamp = DateTime.UtcNow,
        });
    }
}
