namespace Domain.Common.Config.AI.Identity;

/// <summary>
/// Config for the federated workload-identity credential provider. Used when the
/// host federates with Entra via OIDC — typically AKS workload identity, GitHub
/// Actions OIDC, or Azure DevOps OIDC. Preferred over all other kinds when available
/// because no secret is stored anywhere.
/// </summary>
public class FederatedProviderConfig
{
    /// <summary>
    /// Stable agent id stamped onto the returned <c>AgentIdentity</c>. When null or
    /// whitespace, the provider treats itself as unconfigured.
    /// </summary>
    public string? AgentId { get; set; }

    /// <summary>
    /// Entra tenant id the workload-identity exchange targets. Required.
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Entra application client id the agent runs as. Required.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Filesystem path to the federated token. When null, the Azure SDK's
    /// <c>WorkloadIdentityCredential</c> resolves the path from the
    /// <c>AZURE_FEDERATED_TOKEN_FILE</c> environment variable (the AKS default).
    /// Set this only when the runtime requires a non-standard token-file location.
    /// </summary>
    public string? TokenFilePath { get; set; }

    /// <summary>
    /// Optional Entra service principal object id stamped onto the resulting identity
    /// for audit attribution.
    /// </summary>
    public string? ObjectId { get; set; }
}
