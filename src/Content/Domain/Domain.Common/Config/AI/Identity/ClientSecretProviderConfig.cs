namespace Domain.Common.Config.AI.Identity;

/// <summary>
/// Config for the client-secret credential provider — explicit last-resort. Stores a
/// long-lived secret with no automatic rotation. Always prefer federated, managed
/// identity, or certificate over this; the provider emits a startup warning when
/// configured in any environment other than Development.
/// </summary>
/// <remarks>
/// <see cref="ClientSecret"/> must NEVER be persisted in <c>appsettings.json</c>.
/// Use user-secrets (development) or Azure Key Vault (production). The provider
/// scrubs the secret from all log output.
/// </remarks>
public class ClientSecretProviderConfig
{
    /// <summary>
    /// Stable agent id stamped onto the returned <c>AgentIdentity</c>. When null or
    /// whitespace, the provider treats itself as unconfigured.
    /// </summary>
    public string? AgentId { get; set; }

    /// <summary>Entra tenant id. Required.</summary>
    public string? TenantId { get; set; }

    /// <summary>Entra application client id. Required.</summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// The client secret used for token exchange. Required. Persist via
    /// user-secrets / Key Vault, never appsettings.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Optional Entra service principal object id stamped onto the resulting identity
    /// for audit attribution.
    /// </summary>
    public string? ObjectId { get; set; }
}
