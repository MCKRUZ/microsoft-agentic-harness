namespace Infrastructure.AI.Attestation;

/// <summary>
/// Configuration options for HMAC attestation key material.
/// Keys are sourced from User Secrets (development) or Azure Key Vault (production).
/// Never store actual key material in appsettings.json.
/// </summary>
public sealed class AttestationKeyOptions
{
    /// <summary>Configuration section path.</summary>
    public const string SectionName = "Attestation";

    /// <summary>
    /// Ordered list of HMAC keys. Newest keys first.
    /// At least one entry is required for the attestation service to operate.
    /// </summary>
    public IReadOnlyList<HmacKeyEntry> HmacKeys { get; init; } = [];

    /// <summary>
    /// Version identifier of the key used for new signatures.
    /// Must match a <see cref="HmacKeyEntry.Version"/> in <see cref="HmacKeys"/>.
    /// </summary>
    public string CurrentKeyVersion { get; init; } = string.Empty;
}

/// <summary>
/// A single HMAC signing key with its version identifier.
/// </summary>
public sealed class HmacKeyEntry
{
    /// <summary>Unique version identifier (e.g., "v1", "2024-01-15").</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Base64-encoded HMAC-SHA256 key. Minimum 32 bytes when decoded.</summary>
    public string Key { get; init; } = string.Empty;
}
