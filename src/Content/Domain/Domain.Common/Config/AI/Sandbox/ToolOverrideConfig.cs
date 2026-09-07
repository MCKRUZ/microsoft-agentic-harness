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
/// (#418) carry the identical limitation on one further path, tracked for follow-up rather than
/// covered by #418 itself:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>ToolPermissionProfileResolver.ResolveForUngovernedDispatch</c> — used by whole-shell-command
/// tools like <c>WorkspaceCommandRunner</c>/<c>IacSandboxRunner</c> that bypass
/// <c>ICapabilityEnforcer</c> entirely — never copies these four fields onto the profile it returns,
/// so configuring them against a tool reached only through that path is silently inert.
/// </description></item>
/// </list>
/// <para>
/// The plan/DAG executor (<c>ToolUseStepExecutor</c>) used to have this same gap — it admitted a
/// step's tool call without extracting a <c>ToolCallResourceRequest</c> the way
/// <c>GovernedAIFunction</c> and <c>DirectToolInvoker</c> already did (#418 only wired those two
/// entry points at the time). #587 closed it: <c>ToolUseStepExecutor.ExtractResourceRequest</c> now
/// populates it the same way, so a call whose paths/hosts came from the step's own declared
/// parameters is scoped identically across all three entry points.
/// </para>
/// <para>
/// <strong>The narrower gap that once existed here is closed:</strong> when a path/host value is
/// chained from an upstream step's output rather than declared directly on the step, it arrives via
/// <c>ToolUseStepExecutor.BuildToolArguments</c>' JSON-merge. That merge used to call
/// <c>JsonElement.GetRawText()</c> unconditionally, keeping literal quote characters on a chained
/// string value and failing path/host normalization every time — refused, never silently allowed
/// (#587's <c>ResourceParameterExtractor.Extract</c> fix treats a value it cannot read as unknown
/// rather than "nothing to check"), but unusable for scoping. #595 fixed the merge itself
/// (<c>ToolParameters.NormalizeScalarToText</c> unwraps a string value the same way a directly-declared
/// parameter arrives), so a chained path/host now scopes identically to a directly-declared one.
/// </para>
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
