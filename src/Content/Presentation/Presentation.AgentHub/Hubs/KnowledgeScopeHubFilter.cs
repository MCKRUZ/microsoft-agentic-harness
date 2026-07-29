using Application.AI.Common.Interfaces.KnowledgeGraph;
using Microsoft.AspNetCore.SignalR;
using Presentation.Common.Scoping;

namespace Presentation.AgentHub.Hubs;

/// <summary>
/// SignalR hub filter that establishes the knowledge scope (user + tenant) from the authenticated
/// <see cref="HubCallerContext.User"/> around every hub operation — connect, method invocation, and
/// disconnect. The HTTP <c>KnowledgeScopeMiddleware</c> does not cover them (they run on their own DI
/// scope with no HTTP request), so this is the equivalent chokepoint for the SignalR transport.
/// </summary>
/// <remarks>
/// <para>
/// All three lifetime points are covered on purpose. Covering only method invocations would leave the
/// connect and disconnect handlers running with whatever scope happened to be ambient — which, for a
/// fresh SignalR execution context, is none. None does not mean "restricted"; an unscoped write is
/// stored as global and is therefore readable by everyone. Today's handlers write no owner-stamped
/// state, so that gap was latent rather than live, but a filter that protects two of three entry
/// points is one refactor away from being wrong, and the failure would be silent.
/// </para>
/// <para>
/// <see cref="IKnowledgeScopeWriter"/> is resolved from the operation's own
/// <c>ServiceProvider</c> — the same scope from which the hub's orchestrator and the downstream
/// MediatR handler/graph-store decorators resolve <see cref="IKnowledgeScope"/> — so the value set
/// here is the value they observe.
/// </para>
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

        using var scopeToken = BeginScope(invocationContext.ServiceProvider, invocationContext.Context);

        // Disposing after the hub method restores the previously ambient identity, so scope set for one
        // invocation can never survive into the connection's next invocation.
        return await next(invocationContext);
    }

    /// <inheritdoc />
    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        using var scopeToken = BeginScope(context.ServiceProvider, context.Context);
        await next(context);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Unlike the other two, this one cannot reject: a disconnect has already happened and refusing it
    /// would only strand the connection's teardown. So an unresolvable identity here runs the handler
    /// unscoped rather than throwing — safe precisely because a disconnect handler must not write
    /// owner-stamped state, and the connect path above has already refused any connection whose
    /// identity could not be resolved.
    /// </remarks>
    public async Task OnDisconnectedAsync(
        HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        IDisposable? scopeToken = null;
        if (context.ServiceProvider.GetService(typeof(IKnowledgeScopeWriter)) is IKnowledgeScopeWriter writer)
            KnowledgeScopeInitializer.TryApply(context.Context.User, writer, out scopeToken);

        using (scopeToken)
        {
            await next(context, exception);
        }
    }

    /// <summary>
    /// Establishes the ambient knowledge scope for one hub operation, or rejects the caller.
    /// </summary>
    /// <remarks>
    /// Same fail-closed rule as the HTTP middleware: an authenticated caller whose identity cannot be
    /// resolved must not run unscoped, because an unscoped write is a globally readable one.
    /// <see cref="HubException"/> is SignalR's client-visible rejection; its message is deliberately
    /// non-specific. Returns a no-op token when no <see cref="IKnowledgeScopeWriter"/> is registered,
    /// which is the knowledge-graph-disabled configuration.
    /// </remarks>
    private static IDisposable? BeginScope(IServiceProvider services, HubCallerContext caller)
    {
        if (services.GetService(typeof(IKnowledgeScopeWriter)) is not IKnowledgeScopeWriter scopeWriter)
            return null;

        if (!KnowledgeScopeInitializer.TryApply(caller.User, scopeWriter, out var scopeToken))
            throw new HubException("The authenticated principal carries no usable identity.");

        return scopeToken;
    }
}
