namespace Domain.Common.Config.AI.BundleExecution;

/// <summary>
/// The per-caller capability-grant table for bundle runs. Resolution precedence is
/// <see cref="BySubject"/> (exact credential) → <see cref="ByRole"/> (least-privilege combination of the
/// caller's matching roles) → <see cref="Default"/>. Bound from
/// <c>AppConfig:AI:BundleExecution:Envelopes</c>.
/// </summary>
/// <remarks>
/// <para>
/// Envelopes are keyed to the caller's identity because the host runs externally-authored agents on
/// behalf of many callers with different trust: one caller may be granted a broad tool/MCP surface while
/// another is confined to a read-only slice. Keeping the grant table in configuration (not in the bundle)
/// is what makes the bundle's own declarations mere requests.
/// </para>
/// <para>
/// <strong>Fail-closed default.</strong> When no subject and no role matches, the <see cref="Default"/>
/// envelope applies — and an unconfigured <see cref="Default"/> grants nothing. A host that enables
/// bundle execution without configuring envelopes therefore denies every capability rather than opening up.
/// </para>
/// </remarks>
public class CapabilityEnvelopesConfig
{
    /// <summary>
    /// The grant applied when the caller matches no subject and no role entry. Defaults to a fail-closed
    /// envelope that grants nothing.
    /// </summary>
    public CapabilityEnvelopeConfig Default { get; set; } = new();

    /// <summary>
    /// Per-credential grants, keyed by the caller's subject claim. An exact subject match takes precedence
    /// over any role match. Keys are matched case-insensitively by the resolver.
    /// </summary>
    public Dictionary<string, CapabilityEnvelopeConfig> BySubject { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-role grants, keyed by role name. When a caller has several matching roles the resolver combines
    /// them to the <em>least-privilege</em> result (intersection of tools and MCP servers, minimum
    /// autonomy) so overlapping roles can never widen a grant. Keys are matched case-insensitively.
    /// </summary>
    public Dictionary<string, CapabilityEnvelopeConfig> ByRole { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
