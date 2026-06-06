namespace Domain.Common.Config.AI.Identity;

/// <summary>
/// Config for the Azure Managed Identity credential provider. Used when the host
/// runs in Azure (App Service, Container Apps, AKS, VM) with either a system-assigned
/// or user-assigned managed identity.
/// </summary>
public class ManagedIdentityProviderConfig
{
    /// <summary>
    /// Stable agent id stamped onto the returned <c>AgentIdentity</c>. When null or
    /// whitespace, the provider treats itself as unconfigured and the resolver moves
    /// on to the next kind in the hierarchy.
    /// </summary>
    public string? AgentId { get; set; }

    /// <summary>
    /// Optional tenant id surfaced on the resulting identity. The Azure
    /// <c>ManagedIdentityCredential</c> derives the tenant from IMDS at token-acquisition
    /// time, so this field is only used for diagnostics and audit metadata.
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Optional client id for a user-assigned managed identity. Null indicates the
    /// system-assigned identity.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Optional Entra service principal object id stamped onto the resulting identity
    /// for audit attribution. The provider does not validate it against IMDS — that
    /// would require an outbound call.
    /// </summary>
    public string? ObjectId { get; set; }
}
