namespace Domain.Common.Config.Azure;

/// <summary>
/// Azure Key Vault configuration for secret management.
/// </summary>
public class KeyVaultConfig
{
    /// <summary>
    /// Gets or sets the Key Vault URI (e.g., "https://yourvault.vault.azure.net/").
    /// When null, Key Vault health checks are disabled.
    /// </summary>
    public string? VaultUri { get; set; }

    /// <summary>
    /// Gets or sets the credential configuration for Key Vault access.
    /// </summary>
    public EntraCredentialConfig Token { get; set; } = new();
}
