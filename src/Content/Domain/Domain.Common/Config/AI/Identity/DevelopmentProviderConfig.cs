namespace Domain.Common.Config.AI.Identity;

/// <summary>
/// Fixture-identity config for <c>DevelopmentAgentCredentialProvider</c>. Honoured only
/// when the host environment is Development. In production, the resolver refuses to
/// invoke the Development provider regardless of what this config contains.
/// </summary>
/// <remarks>
/// The Development provider is a test/dev escape hatch — it returns a static identity
/// without contacting Entra. Use it locally so the rest of the harness behaves
/// identically with the identity subsystem on, even before Entra Agent ID is wired up.
/// </remarks>
public class DevelopmentProviderConfig
{
    /// <summary>
    /// The fixture agent id stamped onto the returned <c>AgentIdentity</c>. Defaults to
    /// <c>"dev-agent"</c>. Override to disambiguate when multiple developers share a
    /// dev environment.
    /// </summary>
    public string AgentId { get; set; } = "dev-agent";

    /// <summary>
    /// Optional fixture tenant id. Null when the dev environment doesn't simulate
    /// multi-tenant isolation. When set, the resulting identity carries this tenant
    /// so multi-tenant assertions and RBAC checks have something to compare against.
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Optional fixture object id (Entra service principal object id stand-in). Mainly
    /// useful when a downstream component reads <c>AgentIdentity.ObjectId</c> for
    /// audit attribution and you want the dev-mode value to be distinguishable from
    /// production.
    /// </summary>
    public string? ObjectId { get; set; }
}
