using Azure.Core;
using Azure.Identity;
using Domain.Common.Config.Azure;

namespace Application.Common.Factories;

/// <summary>
/// Creates Azure credential instances from <see cref="EntraCredentialConfig"/> configuration.
/// </summary>
/// <remarks>
/// <para>Credential selection priority:</para>
/// <list type="number">
///   <item>If <c>ClientId</c> + <c>ClientSecret</c> + <c>TenantId</c> are all set: <see cref="ClientSecretCredential"/>.</item>
///   <item>If <c>ClientId</c> + <c>CertificatePath</c> + <c>TenantId</c> are all set: <see cref="ClientCertificateCredential"/>.</item>
///   <item>Otherwise: <see cref="DefaultAzureCredential"/> (managed identity, VS credential, CLI credential, etc.).</item>
/// </list>
/// <para>
/// <strong>VDI gotcha:</strong> on a corporate VDI, the fallback <see cref="DefaultAzureCredential"/>
/// chain can silently succeed via the VDI host's own <see cref="ManagedIdentityCredential"/>
/// instead of reaching the developer's own credential — the app runs, but authenticates as the
/// wrong identity. Set <see cref="EntraCredentialConfig.ExcludeManagedIdentityCredential"/> to
/// skip that step on affected VDIs. See the identity-and-access documentation for details.
/// </para>
/// </remarks>
public static class AzureCredentialFactory
{
    /// <summary>
    /// Creates a <see cref="TokenCredential"/> from the given Entra ID configuration.
    /// Falls back to <see cref="DefaultAzureCredential"/> when explicit credentials are not fully configured.
    /// </summary>
    /// <param name="config">The Entra credential configuration.</param>
    /// <returns>A configured <see cref="TokenCredential"/>.</returns>
    public static TokenCredential CreateTokenCredential(EntraCredentialConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // Client secret authentication
        if (!string.IsNullOrWhiteSpace(config.TenantId) &&
            !string.IsNullOrWhiteSpace(config.ClientId) &&
            !string.IsNullOrWhiteSpace(config.ClientSecret))
        {
            return new ClientSecretCredential(config.TenantId, config.ClientId, config.ClientSecret);
        }

        // Certificate-based authentication
        if (!string.IsNullOrWhiteSpace(config.TenantId) &&
            !string.IsNullOrWhiteSpace(config.ClientId) &&
            !string.IsNullOrWhiteSpace(config.CertificatePath))
        {
            return new ClientCertificateCredential(config.TenantId, config.ClientId, config.CertificatePath);
        }

        // Default credential chain (managed identity, VS, CLI, etc.)
        return new DefaultAzureCredential(BuildDefaultAzureCredentialOptions(config));
    }

    /// <summary>
    /// Maps <see cref="EntraCredentialConfig"/> onto the <see cref="DefaultAzureCredentialOptions"/>
    /// used for the fallback credential chain.
    /// </summary>
    /// <param name="config">The Entra credential configuration.</param>
    /// <returns>The mapped options.</returns>
    public static DefaultAzureCredentialOptions BuildDefaultAzureCredentialOptions(EntraCredentialConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var options = new DefaultAzureCredentialOptions();

        if (!string.IsNullOrWhiteSpace(config.TenantId))
            options.TenantId = config.TenantId;

        if (!string.IsNullOrWhiteSpace(config.ClientId))
            options.ManagedIdentityClientId = config.ClientId;

        options.ExcludeManagedIdentityCredential = config.ExcludeManagedIdentityCredential;

        return options;
    }
}
