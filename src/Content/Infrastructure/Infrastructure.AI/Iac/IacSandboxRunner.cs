using Application.AI.Common.Interfaces.Sandbox;
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
/// The permission profile grants <see cref="ToolCapability.FileRead"/>,
/// <see cref="ToolCapability.FileWrite"/> (terraform writes a <c>.terraform</c>
/// cache and a plan file; bicep emits an ARM template), <see cref="ToolCapability.Subprocess"/>
/// (the CLIs spawn provider plugins / language servers), and
/// <see cref="ToolCapability.NetworkAccess"/> scoped to the provider/module
/// registries via <see cref="ToolPermissionProfile.AllowedHosts"/>. The
/// filesystem scope is the single module directory.
/// </para>
/// <para>
/// Egress enforcement is the registry allowlist from
/// <c>AppConfig.AI.Iac.RegistryAllowlist</c>. Because the IaC generators dispatch
/// directly through the keyed sandbox executor (bypassing the MediatR
/// <c>ToolPermissionBehavior</c>/<c>CapabilityEnforcer</c> pipeline), the only
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
    /// Runs an IaC CLI inside the sandbox rooted at <paramref name="moduleDirectory"/>.
    /// </summary>
    /// <param name="program">The CLI program to launch (e.g. <c>terraform</c>, <c>bicep</c>, <c>checkov</c>).</param>
    /// <param name="arguments">The discrete CLI arguments — each entry is passed verbatim, never shell-interpreted.</param>
    /// <param name="moduleDirectory">The sandbox-rooted directory the CLI runs against; the sole allowed filesystem path.</param>
    /// <param name="registryAllowlist">The provider/module-registry hosts the run may reach. Seeds the sandbox egress allowlist.</param>
    /// <param name="executor">The sandbox executor to dispatch through.</param>
    /// <param name="toolName">Tool name for diagnostic attribution in the sandbox request.</param>
    /// <param name="timeout">Optional wall-clock timeout. Defaults to 5 minutes — terraform init/plan can be slow.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The raw <see cref="SandboxExecutionResult"/> for the caller to parse.</returns>
    public static async Task<SandboxExecutionResult> RunAsync(
        string program,
        IReadOnlyList<string> arguments,
        string moduleDirectory,
        IReadOnlyList<string> registryAllowlist,
        ISandboxExecutor executor,
        string toolName,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(program);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleDirectory);
        ArgumentNullException.ThrowIfNull(registryAllowlist);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        var profile = new ToolPermissionProfile
        {
            RequiredCapabilities =
                ToolCapability.FileRead
                | ToolCapability.FileWrite
                | ToolCapability.Subprocess
                | ToolCapability.NetworkAccess,
            AllowedPaths = [moduleDirectory],
            AllowedPrograms = [program],
            AllowedHosts = registryAllowlist,
            DeniedHosts = [],
            DeniedPaths = [],
            MinimumIsolation = SandboxIsolationLevel.Process
        };

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
