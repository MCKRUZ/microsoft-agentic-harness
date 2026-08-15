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

    /// <summary>
    /// Gets or sets whether <c>ManagedIdentityCredential</c> is excluded from the
    /// <c>DefaultAzureCredential</c> fallback chain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On a corporate VDI, the VDI host itself often has a managed identity. When that's true,
    /// <c>DefaultAzureCredential</c> can silently authenticate as the VDI host's identity instead
    /// of falling through to the developer's own credential (Azure CLI, Visual Studio, etc.) —
    /// the app runs, but every call is scoped to the wrong identity, producing 403s that look
    /// like a permissions bug. Set this to <see langword="true"/> for local-dev configuration
    /// (e.g. <c>appsettings.Development.json</c>) on affected VDIs to skip
    /// <c>ManagedIdentityCredential</c> and let the chain reach the developer's credential.
    /// </para>
    /// </remarks>
    public bool ExcludeManagedIdentityCredential { get; set; }
}
