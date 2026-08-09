namespace Domain.Common.Config.AI.Identity;

/// <summary>
/// Per-agent tool-invocation allowlist consumed by
/// <c>EntraAgentIdentityValidator</c>. Maps an <c>AgentIdentity.Id</c> to the set
/// of tool keys (as registered in the keyed-DI tool registry) that identity is
/// permitted to invoke.
/// </summary>
/// <remarks>
/// <para>
/// Fail-closed by design: an agent id not present in <see cref="AllowedToolsByAgentId"/>
/// is denied every tool. An agent id mapped to an empty list is denied every tool.
/// The wildcard <c>"*"</c> in a list grants access to every tool (useful for
/// privileged operator agents during incident response).
/// </para>
/// <para>
/// Tool keys are matched case-sensitively to match the keyed-DI registration
/// convention (<c>"file_system"</c>, <c>"calculation_engine"</c>, etc.).
/// AgentIds are matched case-insensitively because Entra app names and
/// configuration-file casing drift in practice.
/// </para>
/// <para>
/// PR-1 ships a static-config implementation. A future PR may swap to a
/// dynamic <c>IToolPermissionStore</c> backed by a database or policy engine
/// without changing the validator's contract.
/// </para>
/// </remarks>
public class ToolAuthorizationConfig
{
    /// <summary>
    /// The wildcard token that, when present in an agent's allowlist, grants access
    /// to all tools regardless of key. Use sparingly — typically only for break-glass
    /// operator agents.
    /// </summary>
    public const string WildcardToken = "*";

    /// <summary>
    /// Master switch for per-agent tool authorization. <c>false</c> by default: the
    /// admission chain's authorization stage reports itself off and admits every call,
    /// which is the harness's behaviour for every release before this switch existed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately separate from <see cref="AgentIdentityConfig.Enabled"/>. That switch
    /// answers "does this agent carry an Entra-bound workload identity for outbound
    /// calls"; this one answers "is that identity used to decide which tools the agent
    /// may invoke here". A consumer may want the first without the second — acquiring a
    /// managed identity to call Azure is not a statement about local tool policy — so
    /// coupling them would force an allowlist on consumers who only wanted a token.
    /// </para>
    /// <para>
    /// Turning this on has hard prerequisites, all enforced at startup by
    /// <c>ToolAuthorizationConfigValidator</c> rather than discovered as a silent
    /// total denial at the first tool call: <see cref="AgentIdentityConfig.Enabled"/>
    /// must also be on (with it off there is no identity to authorize, and every call
    /// would be refused), and <see cref="AllowedToolsByAgentId"/> must name at least
    /// one agent.
    /// </para>
    /// <para>
    /// The allowlist is keyed by tool key, and the plan engine's capability gates
    /// (<c>llm_call</c>, <c>rag_retrieval</c>) pass through the same chain under those
    /// names. An agent that runs plans needs them listed — or the wildcard — alongside
    /// its real tools.
    /// </para>
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>
    /// Per-agent allowlists keyed by <c>AgentIdentity.Id</c>. AgentId matching is
    /// case-insensitive; tool-key matching is case-sensitive.
    /// </summary>
    public Dictionary<string, IReadOnlyList<string>> AllowedToolsByAgentId { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
}
