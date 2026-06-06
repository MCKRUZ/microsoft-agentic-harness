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
    /// Per-agent allowlists keyed by <c>AgentIdentity.Id</c>. AgentId matching is
    /// case-insensitive; tool-key matching is case-sensitive.
    /// </summary>
    public Dictionary<string, IReadOnlyList<string>> AllowedToolsByAgentId { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);
}
