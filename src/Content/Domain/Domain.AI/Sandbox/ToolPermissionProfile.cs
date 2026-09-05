namespace Domain.AI.Sandbox;

/// <summary>
/// Declares a tool's capability requirements and access scoping rules.
/// </summary>
/// <remarks>
/// <see cref="RequiredCapabilities"/> and <see cref="DeniedCapabilities"/> are kept as two
/// separate fields rather than one pre-folded value — see <see cref="EffectiveCapabilities"/>
/// for why (#405). <c>RequiredCapabilities</c> is always the tool's undiminished declaration;
/// an operator's per-tool deny only ever narrows what gets <em>granted</em>, via
/// <c>CapabilityEnforcer</c>, never what the profile itself claims the tool needs.
/// </remarks>
public sealed record ToolPermissionProfile
{
    /// <summary>
    /// Capabilities the tool requires to execute — the tool's own undiminished declaration,
    /// never reduced by <see cref="DeniedCapabilities"/>. See <see cref="EffectiveCapabilities"/>
    /// for the value sandbox provisioning should read instead.
    /// </summary>
    public required ToolCapability RequiredCapabilities { get; init; }

    /// <summary>
    /// Capabilities an operator has denied this tool via <c>ToolOverrideConfig.DeniedCapabilities</c>,
    /// kept separate from <see cref="RequiredCapabilities"/> so a caller can distinguish "the tool
    /// doesn't need this" from "the tool needs this but was denied it" (#405).
    /// </summary>
    public ToolCapability DeniedCapabilities { get; init; } = ToolCapability.None;

    /// <summary>
    /// The capabilities that should actually be provisioned for this tool: what it requires,
    /// minus what an operator has denied. Sandbox launch preparers and the attestation signer
    /// should read this, not <see cref="RequiredCapabilities"/> — reading the undiminished
    /// requirement there would re-open container egress an operator explicitly closed (#405).
    /// </summary>
    public ToolCapability EffectiveCapabilities => RequiredCapabilities & ~DeniedCapabilities;

    /// <summary>Programs the tool is allowed to spawn as subprocesses.</summary>
    public IReadOnlyList<string> AllowedPrograms { get; init; } = [];

    /// <summary>
    /// Minimum sandbox isolation level required for this tool.
    /// The capability enforcer will never downgrade below this level.
    /// </summary>
    public SandboxIsolationLevel MinimumIsolation { get; init; } = SandboxIsolationLevel.Process;

    /// <summary>
    /// Filesystem-path boundaries a requested path must fall within, when non-empty. Deny-overrides-allow:
    /// checked before <see cref="AllowedPaths"/>, and a match here refuses the call regardless of any
    /// allow entry — in EITHER direction: the requested path falling under a denied boundary, or a
    /// denied boundary sitting under the requested path. The latter direction matters for a
    /// subtree-reading operation (e.g. <c>file_system</c>'s <c>search</c>/<c>list</c>, which walk
    /// everything beneath the single declared path argument) — without it, naming an ANCESTOR of a
    /// denied directory would silently read through the deny rather than triggering it, because the
    /// one string this profile validates was never itself inside the denied boundary. See
    /// <c>CapabilityEnforcer.EnforceAsync</c> for the matching algorithm (#418).
    /// </summary>
    public IReadOnlyList<string> DeniedPaths { get; init; } = [];

    /// <summary>
    /// Filesystem-path boundaries a requested path must fall within. Empty means unrestricted
    /// (subject to <see cref="DeniedPaths"/>) — only a non-empty list requires membership (#418).
    /// </summary>
    public IReadOnlyList<string> AllowedPaths { get; init; } = [];

    /// <summary>
    /// Network-host patterns (exact or <c>*.suffix</c> wildcard) a requested host must match, when
    /// non-empty. Deny-overrides-allow, mirroring <see cref="DeniedPaths"/>/<see cref="AllowedPaths"/> (#418).
    /// </summary>
    public IReadOnlyList<string> DeniedHosts { get; init; } = [];

    /// <summary>
    /// Network-host patterns a requested host must match. Empty means unrestricted (subject to
    /// <see cref="DeniedHosts"/>) — only a non-empty list requires membership (#418).
    /// </summary>
    public IReadOnlyList<string> AllowedHosts { get; init; } = [];
}
