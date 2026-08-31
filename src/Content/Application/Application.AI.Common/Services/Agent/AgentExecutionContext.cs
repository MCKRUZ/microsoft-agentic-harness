using Application.AI.Common.Interfaces.Agent;
using Domain.AI.Identity;

namespace Application.AI.Common.Services.Agent;

/// <summary>
/// Scoped ambient context carrying the identity of the currently executing agent.
/// Set once per request by <see cref="Application.AI.Common.MediatRBehaviors.AgentContextPropagationBehavior{TRequest, TResponse}"/>
/// and consumed by downstream behaviors, handlers, and services.
/// </summary>
/// <remarks>
/// Registered as <c>Scoped</c> in DI — each MediatR request scope gets its own instance.
/// Properties remain <c>null</c> for non-agent requests.
/// </remarks>
public sealed class AgentExecutionContext : IAgentExecutionContext
{
    // Single gate for both Initialize and SetIdentity so the interface's
    // documented thread-safety contract holds: "multiple concurrent agent
    // requests may execute within overlapping async contexts." Without
    // locking, check-then-set on _initialized / AgentIdentity is a TOCTOU
    // window in which two writers with different values both pass the check
    // and the last writer silently wins.
    private readonly object _gate = new();
    private bool _initialized;

    // Computed once, at construction — before Initialize is ever called, and independent of
    // whatever it's later called with. This scope must exist and be stable even for a caller
    // that never calls Initialize at all (nothing today does that, but ToolResultScopeId itself
    // must not throw or return null just because initialization hasn't happened yet).
    private readonly string _fallbackToolResultScopeId = Guid.NewGuid().ToString("N");

    // Freezes ToolResultScopeId's answer on first read (#562). Without this, a read before
    // Initialize() and a read after it can observe two different values — CallOnceScopeId ??
    // _fallbackToolResultScopeId flips the moment Initialize supplies a non-null scope — and
    // anything spilled under the first value becomes permanently unfindable under the second,
    // silently: the tool-result store makes "wrong scope" indistinguishable from "never existed".
    // Not reachable today (every Initialize call site runs before any tool call), but nothing
    // enforced that ordering; this makes the guarantee explicit instead of incidental.
    private string? _observedToolResultScopeId;

    /// <inheritdoc />
    public string? AgentId { get; private set; }

    /// <inheritdoc />
    public string? ConversationId { get; private set; }

    /// <inheritdoc />
    public int? TurnNumber { get; private set; }

    /// <inheritdoc />
    public string? CallOnceScopeId { get; private set; }

    /// <inheritdoc />
    public string ToolResultScopeId
    {
        get
        {
            lock (_gate)
            {
                return _observedToolResultScopeId ??= CallOnceScopeId ?? _fallbackToolResultScopeId;
            }
        }
    }

    /// <inheritdoc />
    public bool HasRetrievableToolResultScope => CallOnceScopeId is not null;

    /// <inheritdoc />
    public AgentIdentity? AgentIdentity { get; private set; }

    /// <inheritdoc />
    public void Initialize(string agentId, string conversationId, int turnNumber, string? callOnceScopeId = null)
    {
        lock (_gate)
        {
            // Guard against scope leak: re-initialization with a different agent, conversation, or
            // call-once scope within the same DI scope is always a bug. Only turn number may change
            // (subsequent turns).
            if (_initialized && (AgentId != agentId || ConversationId != conversationId
                || CallOnceScopeId != callOnceScopeId))
                throw new InvalidOperationException(
                    $"AgentExecutionContext scope conflict: already bound to agent '{AgentId}' / " +
                    $"conversation '{ConversationId}' / call-once scope '{CallOnceScopeId}', cannot " +
                    $"re-initialize with agent '{agentId}' / conversation '{conversationId}' / " +
                    $"call-once scope '{callOnceScopeId}'.");

            // ToolResultScopeId was already read (and, per the guard above, this is either the
            // first Initialize call or a value-identical re-initialize) — if the scope id it
            // observed no longer matches what CallOnceScopeId is about to become, that observer
            // is now holding a stale scope. Fail loudly rather than let a spill become orphaned.
            if (_observedToolResultScopeId is not null
                && _observedToolResultScopeId != (callOnceScopeId ?? _fallbackToolResultScopeId))
                throw new InvalidOperationException(
                    $"AgentExecutionContext scope conflict: ToolResultScopeId was already read as " +
                    $"'{_observedToolResultScopeId}' before Initialize supplied call-once scope " +
                    $"'{callOnceScopeId}'. ToolResultScopeId must not be read before Initialize().");

            AgentId = agentId;
            ConversationId = conversationId;
            TurnNumber = turnNumber;
            CallOnceScopeId = callOnceScopeId;
            _initialized = true;
        }
    }

    /// <inheritdoc />
    public void SetIdentity(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        lock (_gate)
        {
            // Same scope-leak guard as Initialize, applied to identity. Re-setting a
            // value-equal identity short-circuits to a literal no-op so the documented
            // idempotent contract holds without a redundant assignment in the setter.
            if (AgentIdentity is not null)
            {
                if (AgentIdentity.Equals(identity))
                    return;

                throw new InvalidOperationException(
                    $"AgentExecutionContext identity conflict: already bound to identity " +
                    $"'{AgentIdentity.Id}' (kind {AgentIdentity.Kind}), cannot re-bind to " +
                    $"identity '{identity.Id}' (kind {identity.Kind}).");
            }

            AgentIdentity = identity;
        }
    }
}
