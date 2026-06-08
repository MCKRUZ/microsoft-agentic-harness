namespace Domain.AI.Attestation;

/// <summary>
/// HMAC-signed proof of tool execution. Provides tamper-evident records of what
/// was executed, what inputs were provided, and what outputs were produced.
/// Supports both success and failure attestations for complete audit trails.
/// </summary>
public sealed record ToolExecutionAttestation
{
    /// <summary>Name of the tool that was executed.</summary>
    public required string ToolName { get; init; }

    /// <summary>SHA-256 hash of the tool input parameters.</summary>
    public required string InputHash { get; init; }

    /// <summary>SHA-256 hash of the tool output. Null if execution crashed before producing output.</summary>
    public string? OutputHash { get; init; }

    /// <summary>When the tool execution occurred.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>HMAC-SHA256 signature covering the attestation fields.</summary>
    public required string Signature { get; init; }

    /// <summary>Version identifier for the signing key, enabling key rotation verification.</summary>
    public required string KeyVersion { get; init; }

    /// <summary>Whether this attestation records a failed execution (no output available).</summary>
    public bool IsFailureAttestation { get; init; }

    /// <summary>Reason for failure. Populated only when <see cref="IsFailureAttestation"/> is true.</summary>
    public string? FailureReason { get; init; }

    /// <summary>
    /// SHA-256 digest of the egress decisions recorded for this execution (the
    /// destinations the harness permitted or denied during sandbox preflight).
    /// Null for baseline attestations produced before egress enforcement; its
    /// presence is the discriminator that selects the extended HMAC payload
    /// shape during verification, so baseline attestations remain verifiable.
    /// </summary>
    public string? EgressDigest { get; init; }
}
