namespace Domain.Common.Config.AI.Identity;

/// <summary>
/// Config for the X.509 client-certificate credential provider. Used for hybrid
/// scenarios where the agent runs outside Azure but still authenticates to Entra ID.
/// </summary>
/// <remarks>
/// The certificate is identified by thumbprint and looked up from a Windows
/// certificate store; consumers running on Linux must supply the certificate path
/// via <see cref="CertificatePath"/> instead. Exactly one of <see cref="CertificateThumbprint"/>
/// or <see cref="CertificatePath"/> must be set.
/// </remarks>
public class CertificateProviderConfig
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
    /// SHA-1 thumbprint of the certificate to load from the Windows certificate store.
    /// Mutually exclusive with <see cref="CertificatePath"/>.
    /// </summary>
    public string? CertificateThumbprint { get; set; }

    /// <summary>
    /// Filesystem path to a PEM- or PFX-encoded certificate file. Mutually exclusive
    /// with <see cref="CertificateThumbprint"/>. Required for Linux deployments.
    /// </summary>
    public string? CertificatePath { get; set; }

    /// <summary>
    /// Optional password for the certificate file at <see cref="CertificatePath"/>.
    /// Persist via Key Vault or user-secrets — never in appsettings.json.
    /// </summary>
    public string? CertificatePassword { get; set; }

    /// <summary>
    /// Optional Entra service principal object id stamped onto the resulting identity
    /// for audit attribution.
    /// </summary>
    public string? ObjectId { get; set; }
}
