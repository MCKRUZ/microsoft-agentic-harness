using Microsoft.Extensions.Logging;

namespace Application.Common.Logging;

/// <summary>
/// An <see cref="IExternalScopeProvider"/> implementation that understands the agentic
/// scope hierarchy (Agent &gt; Conversation &gt; Turn &gt; Tool). All logging providers
/// that consume <c>IExternalScopeProvider</c> automatically receive agent context fields.
/// </summary>
/// <remarks>
/// This scope provider is registered once in DI and shared across all loggers.
/// When application code pushes an <see cref="AgentLogScope"/> via
/// <c>ILogger.BeginScope</c>, the scope data flows through to every formatter
/// and provider without those components needing to know about agent concepts.
/// <para>
/// The implementation delegates to <see cref="LoggerExternalScopeProvider"/>
/// for thread-safe scope management and adds convenience methods for
/// extracting the current <see cref="AgentLogScope"/> from the scope stack.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Extracting current agent context in a formatter:
/// var agentScope = AgentScopeProvider.GetCurrentAgentScope(scopeProvider);
/// if (agentScope?.AgentId is not null)
/// {
///     writer.Write($"[{agentScope.AgentId}] ");
/// }
/// </code>
/// </example>
public sealed class AgentScopeProvider : IExternalScopeProvider
{
    private readonly LoggerExternalScopeProvider _inner = new();

    /// <inheritdoc />
    public void ForEachScope<TState>(Action<object?, TState> callback, TState state) =>
        _inner.ForEachScope(callback, state);

    /// <inheritdoc />
    public IDisposable Push(object? state) =>
        _inner.Push(state);

    /// <summary>
    /// Extracts the merged <see cref="AgentLogScope"/> from the current scope stack
    /// by walking all scopes and combining <c>AgentLogScope</c> entries.
    /// Inner scopes override outer scope properties.
    /// </summary>
    /// <param name="scopeProvider">The scope provider to inspect.</param>
    /// <returns>
    /// A merged <see cref="AgentLogScope"/> with all properties from the scope stack,
    /// or <c>null</c> if no <c>AgentLogScope</c> entries are present.
    /// </returns>
    public static AgentLogScope? GetCurrentAgentScope(IExternalScopeProvider? scopeProvider)
    {
        if (scopeProvider is null)
            return null;

        var accumulator = t_accumulator ??= new ScopeAccumulator();
        accumulator.Reset();

        scopeProvider.ForEachScope(static (scope, state) =>
        {
            if (scope is AgentLogScope agentScope)
                state.MergeFrom(agentScope);
        }, accumulator);

        return accumulator.ToScope();
    }

    [ThreadStatic]
    private static ScopeAccumulator? t_accumulator;

    /// <summary>
    /// Thread-local mutable accumulator that merges <see cref="AgentLogScope"/> entries
    /// from the scope stack without per-call heap allocations. Reused via <c>[ThreadStatic]</c>.
    /// </summary>
    private sealed class ScopeAccumulator
    {
        public string? AgentId;
        public string? ParentAgentId;
        public string? ConversationId;
        public int? TurnNumber;
        public string? ToolName;

        public void MergeFrom(AgentLogScope scope)
        {
            AgentId = scope.AgentId ?? AgentId;
            ParentAgentId = scope.ParentAgentId ?? ParentAgentId;
            ConversationId = scope.ConversationId ?? ConversationId;
            TurnNumber = scope.TurnNumber ?? TurnNumber;
            ToolName = scope.ToolName ?? ToolName;
        }

        public AgentLogScope? ToScope() =>
            AgentId is null && ParentAgentId is null && ConversationId is null
                && TurnNumber is null && ToolName is null
                ? null
                : new AgentLogScope(AgentId, ParentAgentId, ConversationId, TurnNumber, ToolName);

        public void Reset()
        {
            AgentId = null;
            ParentAgentId = null;
            ConversationId = null;
            TurnNumber = null;
            ToolName = null;
        }
    }
}
