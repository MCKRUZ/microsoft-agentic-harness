namespace Domain.Common.Config.Observability;

/// <summary>
/// The Entra agent identity a single named harness agent reports as in Microsoft Agent 365,
/// overriding the host-level default in <see cref="Agent365ExporterConfig"/>.
/// </summary>
/// <remarks>
/// <para>
/// Microsoft's model is one agent identity per deployed agent, so a host that runs more than one
/// distinct agent needs more than one identity for the tenant's inventory to be meaningful. Without
/// an override every agent in the process reports as the host-level default, which collapses them
/// into a single row.
/// </para>
/// <para>
/// There is deliberately no tenant here. Agent identities are single-tenant and can only be issued
/// tokens in the tenant where they were created, so every identity a host can authenticate as
/// necessarily shares that host's tenant. Tenant therefore stays on
/// <see cref="Agent365ExporterConfig.TenantId"/>.
/// </para>
/// </remarks>
public class Agent365AgentIdentityConfig
{
    /// <summary>
    /// Gets or sets the <c>appId</c> of the Entra agent identity this agent reports as. Required,
    /// and must be a GUID.
    /// </summary>
    public string? AppId { get; set; }

    /// <summary>
    /// Gets or sets the <c>appId</c> of the blueprint this agent's identity was created from. Must
    /// be a GUID when set.
    /// </summary>
    /// <remarks>
    /// Set this whenever the agent is a different <em>kind</em> of agent from the host default.
    /// Blueprints group agents of the same kind, so inheriting the host-level blueprint for an agent
    /// minted from a different one would file it under the wrong kind — worse than reporting none,
    /// which is why the host-level value is not inherited when an override is in play.
    /// </remarks>
    public string? BlueprintId { get; set; }
}
