namespace Domain.Common.Config.AI.Identity;

/// <summary>
/// Configuration for the agent-identity subsystem (PR-1).
/// </summary>
/// <remarks>
/// <para>
/// When <see cref="Enabled"/> is <c>false</c> (default), <c>AgentFactory</c> does
/// not resolve an agent identity and <c>IAgentExecutionContext.AgentIdentity</c>
/// stays <c>null</c> — exactly the pre-PR-1 behaviour. Consumers that have not
/// configured Entra Agent ID credentials should leave this off.
/// </para>
/// <para>
/// When <see cref="Enabled"/> is <c>true</c>, <c>AgentFactory</c> resolves an
/// identity via the registered <c>IAgentIdentityResolver</c> at agent construction.
/// Resolution failures and missing-resolver misconfigurations fail loudly — the
/// security guarantee opt-in is binary, not best-effort.
/// </para>
/// </remarks>
public class AgentIdentityConfig
{
    /// <summary>
    /// Master switch for the agent-identity subsystem. <c>false</c> by default so
    /// the harness keeps its pre-PR-1 behaviour until a consumer explicitly opts in.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The OAuth token audience used by the credential resolver when an agent does
    /// not declare its own. Typically the Entra application URI of the agent's
    /// service principal (e.g. <c>"api://harness-agent"</c>). May be left empty
    /// when every registered credential provider derives audience internally.
    /// </summary>
    public string DefaultAudience { get; set; } = string.Empty;

    /// <summary>
    /// Default OAuth scopes requested during token acquisition when an agent does
    /// not declare its own. Empty when the credential flow does not use scopes.
    /// </summary>
    public IReadOnlyList<string> DefaultScopes { get; set; } = [];
}
