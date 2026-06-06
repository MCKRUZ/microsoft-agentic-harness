namespace Domain.AI.Identity;

/// <summary>
/// The credential kind that backs an <see cref="AgentIdentity"/>. Ordering mirrors the
/// resolution preference: federated credentials first (no stored secret), then managed
/// identity (Azure-rotated), then certificate (hybrid), then client secret (last resort).
/// <see cref="Development"/> is a test/dev escape hatch and is never resolved in production.
/// </summary>
/// <remarks>
/// The <see cref="Unspecified"/> sentinel is the default. Treating <c>default</c> as a real
/// kind would let an uninitialised identity pass type checks; the sentinel forces callers
/// to set a kind explicitly. <see cref="IAgentIdentityValidator"/> rejects identities with
/// <see cref="Unspecified"/> as the kind.
/// </remarks>
public enum AgentIdentityKind
{
    /// <summary>
    /// Default sentinel — no kind set. An <see cref="AgentIdentity"/> with this kind is
    /// invalid and will be rejected by validation.
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// Workload identity federation (OIDC). The agent acquires tokens via a federated
    /// credential exchange — no stored secret. Preferred for cloud-hosted agents.
    /// </summary>
    FederatedCredential = 1,

    /// <summary>
    /// Azure managed identity (system-assigned or user-assigned). Azure rotates the
    /// credential automatically; the agent never sees the secret. Preferred for
    /// Azure-hosted runtimes.
    /// </summary>
    ManagedIdentity = 2,

    /// <summary>
    /// Client certificate authentication. Used for hybrid scenarios where the agent
    /// runs outside Azure but still authenticates to Entra ID.
    /// </summary>
    Certificate = 3,

    /// <summary>
    /// Client secret authentication. Explicit last-resort — emits a startup warning
    /// when configured outside Development.
    /// </summary>
    ClientSecret = 4,

    /// <summary>
    /// Development/test escape hatch. Returns a fixture identity without contacting
    /// Entra. Never resolved when the host environment is not Development.
    /// </summary>
    Development = 5
}
