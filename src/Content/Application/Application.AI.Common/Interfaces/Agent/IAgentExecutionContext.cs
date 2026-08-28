using Domain.AI.Identity;

namespace Application.AI.Common.Interfaces.Agent;

/// <summary>
/// Scoped ambient context carrying the identity of the currently executing agent.
/// Set by <c>AgentContextPropagationBehavior</c> and consumed by handlers,
/// services, and other behaviors throughout the request pipeline.
/// </summary>
/// <remarks>
/// Registered as scoped in DI. For non-agent requests, all properties remain <c>null</c>.
/// The implementation must be thread-safe — multiple concurrent agent requests may
/// execute within overlapping async contexts.
/// </remarks>
public interface IAgentExecutionContext
{
    /// <summary>Gets the current agent's unique identifier, or <c>null</c> if not in an agent context.</summary>
    string? AgentId { get; }

    /// <summary>Gets the conversation or session identifier, or <c>null</c>.</summary>
    string? ConversationId { get; }

    /// <summary>Gets the current conversation turn number, or <c>null</c>.</summary>
    int? TurnNumber { get; }

    /// <summary>
    /// Gets the scope <c>ICallOnceGate</c> claims a call-once tool against, or <c>null</c> when
    /// this execution has no scope meaningful for that purpose.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Deliberately not <see cref="ConversationId"/>.</strong> That property already means
    /// three different things to three different callers — the durable conversation an agent turn
    /// belongs to, a fresh-per-call value <c>DirectToolInvoker</c> mints so its retry memory never
    /// carries over between one-shot invocations, and (via a plan run's own conversation-id
    /// fallback) the key a plan run's token budget is reserved and released under, which is
    /// deliberately SHARED across every run of one workflow when the caller supplies none. A
    /// call-once gate reading <see cref="ConversationId"/> directly would inherit whichever of
    /// those three meanings happened to be in scope — unenforceable on the direct-invoke path (a
    /// fresh scope every call defeats "at most once"), and a cross-tenant denial-of-service on the
    /// workflow path (one claim under the shared budget key would permanently block every future
    /// run of that workflow, for every caller). This property exists so each caller states its
    /// call-once scope on its own terms instead of one value being reinterpreted for a purpose it
    /// was never designed for.
    /// </para>
    /// <para>
    /// Null is a legitimate answer, not a gap to fill: a direct tool invocation has no logical
    /// conversation for a repeat call to happen within, so the call-once gate failing open there is
    /// correct — see its remarks on why an absent scope is undefined rather than merely unknown.
    /// </para>
    /// </remarks>
    string? CallOnceScopeId { get; }

    /// <summary>
    /// Gets the isolation boundary spilled tool output is persisted under and must be retrieved
    /// within (#521) — never <c>null</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Deliberately not <see cref="ConversationId"/>, and not simply mirroring
    /// <see cref="CallOnceScopeId"/> either.</strong> A tool-result store is a data-isolation
    /// boundary, not a rate/repeat gate — <see cref="CallOnceScopeId"/>'s own remarks document
    /// that a <see langword="null"/> scope there is a correct, deliberate fail-<em>open</em> for
    /// the direct-invoke path ("no logical conversation for a repeat call to happen within"). The
    /// identical fail-open on a retrieval boundary would mean "no scope enforced" — the exact
    /// ownership hole this property exists to close. This property is therefore <em>never</em>
    /// null: it reuses <see cref="CallOnceScopeId"/>'s value wherever that is already correctly
    /// scoped (the durable conversation for an agent turn, the run id for a plan run — both
    /// already exactly right for "which execution may retrieve this result back"), and falls back
    /// to a freshly generated id, unique to this <see cref="IAgentExecutionContext"/> instance,
    /// only where <see cref="CallOnceScopeId"/> is null. A fresh id is always safe here — narrower
    /// than the true caller-appropriate scope is merely inconvenient (retrieval degrades to
    /// "spilled and immediately gone" for that one call), where wider would be the leak.
    /// </para>
    /// </remarks>
    string ToolResultScopeId { get; }

    /// <summary>
    /// Gets the workload identity of the executing agent, or <c>null</c> when identity
    /// is disabled (<c>AppConfig.AI.Identity.Enabled</c> is false), the call is outside
    /// any agent execution, or the identity has not yet been resolved by
    /// <c>AgentFactory</c>.
    /// </summary>
    /// <remarks>
    /// Separate axis from <see cref="AgentId"/> / <see cref="ConversationId"/>. Those
    /// answer "which agent and which conversation"; <see cref="AgentIdentity"/> answers
    /// "what workload identity does that agent carry for outbound RBAC". The agent id
    /// is a harness label; the identity is the Entra-bound principal.
    /// </remarks>
    AgentIdentity? AgentIdentity { get; }

    /// <summary>
    /// Initializes or updates the execution context with agent identity.
    /// Re-initialization is allowed for subsequent turns within the same agent/conversation
    /// (updates turn number). Throws if called with a different agent, conversation, or
    /// call-once scope, which indicates a scope leak.
    /// </summary>
    /// <param name="agentId">The executing agent's identifier.</param>
    /// <param name="conversationId">The conversation or session identifier.</param>
    /// <param name="turnNumber">The current turn number.</param>
    /// <param name="callOnceScopeId">
    /// The scope to publish as <see cref="CallOnceScopeId"/>. Defaults to <see langword="null"/> —
    /// callers with no call-once-meaningful scope (a direct tool invocation) pass nothing rather
    /// than reusing <paramref name="conversationId"/>; see that property's remarks for why.
    /// </param>
    void Initialize(string agentId, string conversationId, int turnNumber, string? callOnceScopeId = null);

    /// <summary>
    /// Stamps the agent's workload identity onto the execution context. Called once
    /// per agent instance during agent construction (by <c>AgentFactory</c>) after the
    /// <see cref="Application.AI.Common.Interfaces.Identity.IAgentIdentityResolver"/> resolves the identity from the credential
    /// hierarchy.
    /// </summary>
    /// <remarks>
    /// Re-set with a value-equal identity is idempotent (no throw, no state change).
    /// Re-set with a <em>different</em> identity throws
    /// <see cref="InvalidOperationException"/> — the scope is leaking across agent
    /// boundaries and the call site is wrong.
    /// </remarks>
    /// <param name="identity">The resolved agent identity. Must not be null.</param>
    /// <exception cref="ArgumentNullException">Identity is null.</exception>
    /// <exception cref="InvalidOperationException">An identity was already set and the
    /// new one differs from it by value.</exception>
    void SetIdentity(AgentIdentity identity);
}
