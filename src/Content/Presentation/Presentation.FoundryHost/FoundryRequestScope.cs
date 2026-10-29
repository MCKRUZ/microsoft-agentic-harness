using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Services;
using Application.AI.Common.Services.Governance;
using Microsoft.AspNetCore.Http;
using Presentation.Common.Scoping;

namespace Presentation.FoundryHost;

/// <summary>
/// What every inbound request to this host runs inside: its own governance scope, its own record of which tool calls
/// have already been traced, and the knowledge scope of the caller Foundry verified. Factored out of
/// <see cref="Program"/> so it is testable without standing up the web host.
/// </summary>
/// <remarks>
/// <para>
/// This host builds one agent for the whole process and serves concurrent requests through it, and its
/// request loop never goes through <c>ExecuteAgentTurnCommandHandler</c>, which is where every other host
/// arms both of these per turn.
/// </para>
/// <para>
/// <strong>Admission (#478).</strong> Without an ambient pipeline, <c>GovernedAIFunction</c> skips
/// admission, sanitizing, classification, authorization, the loop guard and call-once enforcement.
/// </para>
/// <para>
/// <strong>Tool-call trace claims (#541).</strong> With no ambient <see cref="ReplayedToolCallScope"/>,
/// <c>ToolDiagnosticsMiddleware</c> falls back to one claim set per middleware instance, and this host has
/// one instance for the process. Two unrelated concurrent requests whose first tool call reuses a
/// provider-issued call id (providers number ids per turn and reset them) would share it, and the second
/// request's real tool result would be dropped from its trace. A fresh set per request keeps each
/// request's claims its own, as the turn handler does for every other host.
/// </para>
/// <para>
/// <strong>Owner (#1008).</strong> The request's principal is built from Foundry's verified user id
/// (<see cref="FoundryCallerIdentity"/>) and handed to the same <see cref="KnowledgeScopeMiddleware"/> every web host
/// mounts, so what the agent writes to a shared store carries that caller as owner. The middleware is invoked here
/// rather than mounted, because its <see cref="IKnowledgeScopeWriter"/> lives in the harness's composition root, not in
/// AgentHost's container. A request that names a user who cannot be resolved is refused with 401, never run unowned;
/// one that names nobody runs unscoped, as before.
/// </para>
/// </remarks>
internal static class FoundryRequestScope
{
    /// <summary>Runs <paramref name="next"/> inside a fresh per-request scope, as the caller Foundry verified.</summary>
    /// <param name="provider">
    /// The harness's own composition root, which resolves the scoped admission pipeline and the knowledge-scope writer.
    /// </param>
    /// <param name="context">The inbound request, whose platform user-id header names the caller.</param>
    /// <param name="next">The rest of the request pipeline.</param>
    internal static async Task RunAsync(IServiceProvider provider, HttpContext context, Func<Task> next)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        await using var requestScope = provider.CreateAsyncScope();
        var admissionPipeline = requestScope.ServiceProvider.GetRequiredService<IToolCallAdmissionPipeline>();
        var scopeWriter = requestScope.ServiceProvider.GetRequiredService<IKnowledgeScopeWriter>();

        // The platform header is the only identity this host has; whatever the request carried before is replaced.
        context.User = FoundryCallerIdentity.FromRequest(context.Request);
        var knowledgeScope = new KnowledgeScopeMiddleware(_ => next());

        var previousClaims = ReplayedToolCallScope.Current;
        ReplayedToolCallScope.Current = new ReplayedToolCallSet();
        try
        {
            using (ToolAdmissionAccessor.Begin(admissionPipeline))
            {
                await knowledgeScope.InvokeAsync(context, scopeWriter).ConfigureAwait(false);
            }
        }
        finally
        {
            ReplayedToolCallScope.Current = previousClaims;
        }
    }
}
