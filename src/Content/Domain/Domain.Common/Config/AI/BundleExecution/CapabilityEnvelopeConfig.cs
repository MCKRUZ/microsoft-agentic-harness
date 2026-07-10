namespace Domain.Common.Config.AI.BundleExecution;

/// <summary>
/// The configured grant for one caller (or role, or the default) — the raw, bindable form of a
/// capability envelope. The resolver maps this into the domain <c>CapabilityEnvelope</c> the gate chain
/// enforces. Bound from entries under <c>AppConfig:AI:BundleExecution:Envelopes</c>.
/// </summary>
/// <remarks>
/// This is deliberately a plain settable POCO (string autonomy, mutable lists) so it binds cleanly from
/// configuration; the resolver validates and projects it into the immutable domain value. An
/// unconfigured / empty grant maps to a fail-closed envelope that grants nothing.
/// </remarks>
public class CapabilityEnvelopeConfig
{
    /// <summary>
    /// Tool names the bundle run may invoke. Anything the bundle requests outside this list is denied.
    /// Empty grants no tools.
    /// </summary>
    public List<string> AllowedTools { get; set; } = [];

    /// <summary>
    /// Names of host-registered MCP servers the bundle run may reach. Bundles reference servers by name
    /// only. Empty grants no MCP access.
    /// </summary>
    public List<string> AllowedMcpServers { get; set; } = [];

    /// <summary>
    /// The highest autonomy tier the bundle run may act under, parsed case-insensitively against
    /// <c>Domain.AI.Governance.AutonomyLevel</c> (<c>Restricted</c> / <c>Supervised</c> / <c>Autonomous</c>).
    /// An unset or unrecognised value resolves to the most restrictive tier (<c>Restricted</c>).
    /// </summary>
    /// <value>Default: "Restricted"</value>
    public string AutonomyCeiling { get; set; } = "Restricted";
}
