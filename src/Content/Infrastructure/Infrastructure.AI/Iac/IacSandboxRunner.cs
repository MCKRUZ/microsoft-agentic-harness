using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Services.Sandbox;
using Domain.AI.Sandbox;

namespace Infrastructure.AI.Iac;

/// <summary>
/// Shared dispatch helper for the IaC generators. Builds a
/// <see cref="SandboxExecutionRequest"/> for an infrastructure-as-code CLI
/// (terraform / bicep / checkov / tfsec / arm-ttk), runs it through the supplied
/// <see cref="ISandboxExecutor"/>, and returns the raw
/// <see cref="SandboxExecutionResult"/> for the caller to parse.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <c>WorkspaceCommandRunner</c>: the program and arguments are passed as
/// discrete <c>ArgumentList</c> entries — never a single shell string — so a CLI
/// argument can never smuggle a shell metacharacter through the sandbox boundary.
/// </para>
/// <para>
/// The permission profile grants whatever <see cref="ToolCapability"/> the caller declares — see
/// <c>IacPlanTool.RequiredSandboxCapabilities</c>/<c>IacScanTool.RequiredSandboxCapabilities</c>,
/// the single source of truth this runner used to duplicate as a hardcoded literal (#387). Network
/// access is scoped to the provider/module registries via the egress preflight below
/// (<see cref="SandboxExecutionRequest.EgressPrecheckTargets"/>), not via the permission profile.
/// The module directory itself is not part of the profile at all — the profile's old
/// <c>AllowedPaths</c>/<c>DeniedPaths</c>/<c>AllowedHosts</c>/<c>DeniedHosts</c> were removed as
/// dead config (#405): nothing on this dispatch path (which bypasses
/// <c>CapabilityEnforcer</c> entirely, see below) ever read them.
/// </para>
/// <para>
/// Egress enforcement is the registry allowlist from
/// <c>AppConfig.AI.Iac.RegistryAllowlist</c>. Because the IaC generators dispatch
/// directly through the keyed sandbox executor (bypassing the
/// <c>IToolInvocationGovernor</c>/<c>CapabilityEnforcer</c> governance path), the only
/// active sandbox-side egress gate is the preflight: each allowlisted registry host
/// is surfaced as a <see cref="SandboxExecutionRequest.EgressPrecheckTargets"/>
/// entry so the executor runs every declared destination through the active
/// per-skill <c>IEgressPolicy</c> BEFORE the CLI subprocess is spawned. A policy
/// denial aborts the run with a signed failure attestation; allowed decisions are
/// recorded into the egress audit. This is a declared-target / policy control, not a
/// network namespace — process isolation does not sandbox the subprocess's actual
/// socket connections, so it does not stop a CLI that ignores the declared hosts.
/// Container isolation with a network policy is required for that.
/// </para>
/// </remarks>
public static class IacSandboxRunner
{
    /// <summary>
    /// Runs an IaC CLI inside the sandbox for the module at <paramref name="moduleDirectory"/>.
    /// </summary>
    /// <param name="program">The CLI program to launch (e.g. <c>terraform</c>, <c>bicep</c>, <c>checkov</c>).</param>
    /// <param name="arguments">The discrete CLI arguments — each entry is passed verbatim, never shell-interpreted.</param>
    /// <param name="moduleDirectory">
    /// The module directory the caller resolved <paramref name="requiredCapabilities"/> for. Not
    /// itself an enforced filesystem boundary on this dispatch path — see this class's remarks.
    /// </param>
    /// <param name="registryAllowlist">The provider/module-registry hosts the run may reach. Seeds the sandbox egress allowlist.</param>
    /// <param name="scopedServices">
    /// The per-execution DI scope's provider, used to resolve the keyed-scoped
    /// <see cref="ISandboxExecutor"/> for the effective isolation tier — resolved here, after the
    /// profile, rather than passed in already-resolved (#405 follow-up, a security-review finding
    /// mirroring the identical fix in <c>WorkspaceCommandRunner</c>): the executor must be selected
    /// for the tier the operator's <c>MinimumIsolation</c> override actually resolves to, not a tier
    /// fixed before that override was consulted.
    /// </param>
    /// <param name="defaultIsolationLevel">
    /// The generator's own minimum isolation requirement, independent of any operator override — the
    /// floor this run never drops below even absent a <c>MinimumIsolation</c> override.
    /// </param>
    /// <param name="toolName">Tool name for diagnostic attribution in the sandbox request.</param>
    /// <param name="requiredCapabilities">
    /// The sandbox capabilities this run needs — supplied by the caller (e.g.
    /// <c>IacPlanTool.RequiredSandboxCapabilities</c>) rather than hardcoded here, so there is one
    /// place that states what an <c>iac_plan</c>/<c>iac_scan</c> call may do, not two (#387).
    /// </param>
    /// <param name="permissionResolver">
    /// Resolves the operator's <c>ToolOverrideConfig</c> for <paramref name="toolName"/> — this
    /// runner used to build its permission profile inline, so a per-tool <c>DeniedCapabilities</c>
    /// or <c>MinimumIsolation</c> override never reached it (#405). Via
    /// <see cref="ToolPermissionProfileResolver.ResolveForUngovernedDispatch"/>, which also refuses
    /// outright when the override intersects <paramref name="requiredCapabilities"/> — the CLI never
    /// spawns — matching the governed-call semantics <c>CapabilityEnforcer</c> guarantees rather than
    /// silently narrowing what gets provisioned.
    /// </param>
    /// <param name="timeout">Optional wall-clock timeout. Defaults to 5 minutes — terraform init/plan can be slow.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The raw <see cref="SandboxExecutionResult"/> for the caller to parse.</returns>
    public static async Task<SandboxExecutionResult> RunAsync(
        string program,
        IReadOnlyList<string> arguments,
        string moduleDirectory,
        IReadOnlyList<string> registryAllowlist,
        IServiceProvider scopedServices,
        SandboxIsolationLevel defaultIsolationLevel,
        string toolName,
        ToolCapability requiredCapabilities,
        ToolPermissionProfileResolver permissionResolver,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(program);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleDirectory);
        ArgumentNullException.ThrowIfNull(registryAllowlist);
        ArgumentNullException.ThrowIfNull(scopedServices);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(permissionResolver);

        // Profile and executor resolved together — the ordering invariant (executor only after the
        // profile, at its resolved tier) is now structural inside ResolveExecutorForUngovernedDispatch
        // rather than reproduced here, a /simplify finding: this runner and WorkspaceCommandRunner had
        // independently copy-pasted the same resolve-then-select sequence, with only a comment at each
        // site — not a shared implementation — protecting the ordering the stale-tier bug depended on.
        var dispatchResult = permissionResolver.ResolveExecutorForUngovernedDispatch(
            toolName, requiredCapabilities, [program], scopedServices, defaultIsolationLevel);
        if (!dispatchResult.IsSuccess)
        {
            return new SandboxExecutionResult
            {
                Success = false,
                ErrorMessage = string.Join("; ", dispatchResult.Errors)
            };
        }

        var (profile, executor) = dispatchResult.Value!;

        var request = new SandboxExecutionRequest
        {
            ToolName = toolName,
            Input = string.Empty,
            Command = program,
            ArgumentList = arguments,
            Limits = new ResourceLimits(),
            PermissionProfile = profile,
            Timeout = timeout ?? TimeSpan.FromMinutes(5),
            EgressPrecheckTargets = BuildEgressPrecheckTargets(registryAllowlist)
        };

        return await executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Projects the bare-hostname registry allowlist into concrete
    /// <see cref="Uri"/> targets so the sandbox egress preflight evaluates each
    /// declared registry against the active per-skill <c>IEgressPolicy</c> before the
    /// CLI subprocess is spawned. Without this projection the preflight short-circuits
    /// (it only runs when <see cref="SandboxExecutionRequest.EgressPrecheckTargets"/>
    /// is non-empty) and the documented registry allowlist is never enforced.
    /// </summary>
    /// <param name="registryAllowlist">The provider/module-registry hosts the run may reach.</param>
    /// <returns>
    /// One <c>https://{host}/</c> target per valid host, deduplicated by host. Entries
    /// that are blank or already absolute URIs are normalized to their host; entries
    /// that cannot be parsed as a host are skipped (the startup validator rejects a
    /// malformed allowlist before any run reaches here).
    /// </returns>
    private static IReadOnlyList<Uri> BuildEgressPrecheckTargets(IReadOnlyList<string> registryAllowlist)
    {
        var targets = new List<Uri>(registryAllowlist.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in registryAllowlist)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            var candidate = entry.Contains("://", StringComparison.Ordinal)
                ? entry
                : $"https://{entry.Trim()}/";

            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                && !string.IsNullOrEmpty(uri.Host)
                && seen.Add(uri.Host))
            {
                targets.Add(new Uri($"{uri.Scheme}://{uri.Host}/"));
            }
        }

        return targets;
    }
}
