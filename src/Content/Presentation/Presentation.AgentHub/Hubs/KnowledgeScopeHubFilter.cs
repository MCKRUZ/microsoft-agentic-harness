using Application.AI.Common.Interfaces.KnowledgeGraph;
using Microsoft.AspNetCore.SignalR;
using Presentation.Common.Scoping;

namespace Presentation.AgentHub.Hubs;

/// <summary>
/// SignalR hub filter that establishes the per-invocation knowledge scope (user + tenant) from the
/// authenticated <see cref="HubCallerContext.User"/> before every hub method runs. The HTTP
/// <c>KnowledgeScopeMiddleware</c> does not cover hub method invocations (they run on their own DI
/// scope with no HTTP request), so this is the equivalent chokepoint for the SignalR transport.
/// </summary>
/// <remarks>
/// <see cref="IKnowledgeScopeWriter"/> is resolved from <see cref="HubInvocationContext.ServiceProvider"/>
/// — the same per-invocation scope from which the hub's orchestrator and the downstream MediatR
/// handler/graph-store decorators resolve <see cref="IKnowledgeScope"/> — so the value set here is the
/// value they observe.
/// </remarks>
public sealed class KnowledgeScopeHubFilter : IHubFilter
{
    /// <inheritdoc />
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        ArgumentNullException.ThrowIfNull(invocationContext);
        ArgumentNullException.ThrowIfNull(next);

        if (invocationContext.ServiceProvider.GetService(typeof(IKnowledgeScopeWriter)) is not IKnowledgeScopeWriter scopeWriter)
            return await next(invocationContext);

        // Same fail-closed rule as the HTTP middleware: an authenticated caller whose identity cannot be
        // resolved must not run unscoped, because an unscoped write is a globally readable one. HubException
        // is SignalR's client-visible rejection; its message is deliberately non-specific.
        if (!KnowledgeScopeInitializer.TryApply(invocationContext.Context.User, scopeWriter, out var scopeToken))
            throw new HubException("The authenticated principal carries no usable identity.");

        // Disposing after the hub method restores the previously ambient identity, so scope set for one
        // invocation can never survive into the connection's next invocation.
        using (scopeToken)
        {
            return await next(invocationContext);
        }
    }
}
