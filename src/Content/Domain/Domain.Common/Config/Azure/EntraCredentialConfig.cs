namespace Domain.Common.Config.Azure;

/// <summary>
/// Shared Entra ID (Azure AD) credential configuration used by multiple Azure services.
/// Supports certificate-based and client secret authentication.
/// </summary>
public class EntraCredentialConfig
{
    /// <summary>
    /// Gets or sets the Azure AD tenant ID.
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Gets or sets the application (client) ID.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Gets or sets the client secret for secret-based authentication.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>WARNING:</strong> This value must NEVER be stored in appsettings.json or any file
    /// committed to source control. Use User Secrets (development) or Azure Key Vault (production).
    /// </para>
    /// </remarks>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Gets or sets the path to the certificate for certificate-based authentication.
    /// </summary>
    public string? CertificatePath { get; set; }
}
