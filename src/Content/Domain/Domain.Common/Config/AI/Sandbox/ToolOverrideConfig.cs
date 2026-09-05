namespace Domain.Common.Config.AI.Sandbox;

/// <summary>
/// Per-tool permission override from appsettings, merged with a tool's own
/// <c>ITool.RequiredCapabilities</c> declaration.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DeniedCapabilities"/> genuinely restricts the tool (#405): it is kept apart from
/// the tool's own declaration on <c>ToolPermissionProfile.RequiredCapabilities</c> and only
/// narrows what <c>CapabilityEnforcer</c> will grant and what
/// <c>ToolPermissionProfile.EffectiveCapabilities</c> — the value sandbox provisioning reads —
/// resolves to. It cannot restrict a tool that never runs through the sandbox.
/// </para>
/// <para>
/// <see cref="DeniedPaths"/>/<see cref="AllowedPaths"/>/<see cref="DeniedHosts"/>/<see cref="AllowedHosts"/>
/// (#418) carry the identical limitation on two further paths, tracked for follow-up rather than
/// covered by #418 itself:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>ToolPermissionProfileResolver.ResolveForUngovernedDispatch</c> — used by whole-shell-command
/// tools like <c>WorkspaceCommandRunner</c>/<c>IacSandboxRunner</c> that bypass
/// <c>ICapabilityEnforcer</c> entirely — never copies these four fields onto the profile it returns,
/// so configuring them against a tool reached only through that path is silently inert.
/// </description></item>
/// <item><description>
/// The plan/DAG executor (<c>ToolUseStepExecutor</c>) admits a step's tool call without extracting a
/// <c>ToolCallResourceRequest</c> the way <c>GovernedAIFunction</c> and <c>DirectToolInvoker</c> both
/// do (#418 only wired those two entry points). Configuring path/host scoping for a tool ALSO
/// reachable from a plan step therefore fails that step outright — <c>CapabilityEnforcer</c>'s own
/// fail-closed design (a configured scope with no determined request refuses) has no way to
/// distinguish "unknown" from "this admission path was never taught to ask" here.
/// </description></item>
/// </list>
/// </remarks>
public sealed class ToolOverrideConfig
{
    /// <summary>
    /// Capability names to deny (e.g., "NetworkAccess", "Subprocess"). Narrows what the tool is
    /// granted and what gets provisioned for it — see the class remarks.
    /// </summary>
    public List<string> DeniedCapabilities { get; init; } = [];

    /// <summary>
    /// Minimum isolation level name (e.g., "Process", "Container").
    /// Takes the higher of the tool's own declared <c>ITool.MinimumIsolation</c> and this override
    /// (never downgrades).
    /// </summary>
    public string? MinimumIsolation { get; init; }

    /// <summary>Per-tool memory limit override in MB. Null uses system default.</summary>
    public int? MemoryLimitMb { get; init; }

    /// <summary>Per-tool CPU time override in seconds. Null uses system default.</summary>
    public double? CpuTimeSeconds { get; init; }

    /// <summary>Per-tool execution timeout override in seconds. Null uses system default.</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>Filesystem-path boundaries to deny (#418). Checked before <see cref="AllowedPaths"/>.</summary>
    public List<string> DeniedPaths { get; init; } = [];

    /// <summary>Filesystem-path boundaries a requested path must fall within, when non-empty (#418).</summary>
    public List<string> AllowedPaths { get; init; } = [];

    /// <summary>Network-host patterns to deny (#418, exact or <c>*.suffix</c> wildcard). Checked before <see cref="AllowedHosts"/>.</summary>
    public List<string> DeniedHosts { get; init; } = [];

    /// <summary>Network-host patterns a requested host must match, when non-empty (#418).</summary>
    public List<string> AllowedHosts { get; init; } = [];
}
