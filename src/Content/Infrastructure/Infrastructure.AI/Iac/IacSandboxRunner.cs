using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Services.Sandbox;
using Domain.AI.Sandbox;
using Domain.Common;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Iac;

/// <summary>
/// Shared dispatch helper for the IaC generators. Builds a
/// <see cref="SandboxExecutionRequest"/> for an infrastructure-as-code CLI
/// (terraform / bicep / checkov / tfsec / arm-ttk), runs it through the supplied
/// <see cref="ISandboxExecutor"/>, and returns a <see cref="Result{T}"/> distinguishing a
/// pre-dispatch governance refusal from a completed run — see <see cref="RunAsync"/>'s own
/// <c>returns</c> doc for the exact contract.
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
    /// <param name="logger">
    /// The caller's own logger — used both to record a governance refusal (see
    /// <see cref="MapDispatchFailure{T}"/>, called on the returned <see cref="Result{T}"/>) and, since
    /// #421's /simplify pass, to log a sandbox-level exception here directly rather than in each
    /// generator's own try/catch. The two generators' exception handling around this method used to
    /// be pasted identically at both call sites — the same "pasted, not shared" duplication shape
    /// this issue's own fix exists to close on the refusal path — so it moved here instead.
    /// </param>
    /// <param name="backendLabel">The IaC backend name for log messages (e.g. <c>"Terraform"</c>, <c>"Bicep"</c>).</param>
    /// <param name="timeout">Optional wall-clock timeout. Defaults to 5 minutes — terraform init/plan can be slow.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="Result{T}.Forbidden"/> when the sandbox refused to dispatch the CLI at all — a
    /// governance denial from <see cref="ToolPermissionProfileResolver.ResolveForUngovernedDispatch"/>
    /// (deny-intersection or under-declaration) — never reaching an executor.
    /// <see cref="Result{T}.Fail"/> when the executor itself threw before returning a result (a
    /// misbehaving or unconfigured <see cref="ISandboxExecutor"/> — a template extensibility seam).
    /// Otherwise <see cref="Result{T}.Success"/> wrapping the raw <see cref="SandboxExecutionResult"/>
    /// for the caller to parse, which may itself report a failed CLI run (<c>Success = false</c>) —
    /// that is a genuine dispatch outcome, not a refusal or an exception, and is deliberately not
    /// folded into either of this method's own failure cases.
    /// </returns>
    public static async Task<Result<SandboxExecutionResult>> RunAsync(
        string program,
        IReadOnlyList<string> arguments,
        string moduleDirectory,
        IReadOnlyList<string> registryAllowlist,
        IServiceProvider scopedServices,
        SandboxIsolationLevel defaultIsolationLevel,
        string toolName,
        ToolCapability requiredCapabilities,
        ToolPermissionProfileResolver permissionResolver,
        ILogger logger,
        string backendLabel,
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
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(backendLabel);

        // Profile and executor resolved together — the ordering invariant (executor only after the
        // profile, at its resolved tier) is now structural inside ResolveExecutorForUngovernedDispatch
        // rather than reproduced here, a /simplify finding: this runner and WorkspaceCommandRunner had
        // independently copy-pasted the same resolve-then-select sequence, with only a comment at each
        // site — not a shared implementation — protecting the ordering the stale-tier bug depended on.
        var dispatchResult = permissionResolver.ResolveExecutorForUngovernedDispatch(
            toolName, requiredCapabilities, [program], scopedServices, defaultIsolationLevel);
        if (!dispatchResult.IsSuccess)
        {
            // A distinct Forbidden Result, not a look-alike SandboxExecutionResult{Success=false} —
            // see MapDispatchFailure's remarks for why this replaced the old Attestation-is-null
            // discriminator (#421).
            return Result<SandboxExecutionResult>.Forbidden(string.Join("; ", dispatchResult.Errors));
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

        try
        {
            var executionResult = await executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            return Result<SandboxExecutionResult>.Success(executionResult);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Backend} sandbox run failed for {Program} in {Module}.", backendLabel, program, moduleDirectory);
            return Result<SandboxExecutionResult>.Fail($"Sandbox execution failed: {ex.GetType().Name}.");
        }
    }

    /// <summary>
    /// Maps a failed <see cref="RunAsync"/> dispatch to the caller's own <see cref="Result{T}"/> —
    /// logging and choosing the matching stable failure code. Returns <c>null</c> when
    /// <paramref name="dispatch"/> succeeded, so the caller's own success/real-CLI-failure handling
    /// (inspecting the wrapped <see cref="SandboxExecutionResult"/>) proceeds unchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Centralizes the log-and-return block that used to be pasted at every one of the 7 CLI
    /// dispatch sites across <see cref="TerraformGenerator"/> and <see cref="BicepGenerator"/> —
    /// the same duplication shape that let the <c>plan</c> dispatch in
    /// <see cref="TerraformGenerator.PlanAsync"/> miss the equivalent check for a full commit during
    /// this method's predecessor's own development.
    /// </para>
    /// <para>
    /// <strong>#421:</strong> <see cref="RunAsync"/> used to represent a pre-dispatch governance
    /// refusal as a look-alike <see cref="SandboxExecutionResult"/> (<c>Success = false</c>,
    /// <c>Attestation = null</c>), so every caller had to remember to call a helper re-deriving "was
    /// this a refusal" from that shared field — the same defect shape recorded twice already against
    /// this type in the project's Common Mistakes (first <c>ExitCode</c>, then <c>Attestation</c>; both
    /// corrections picked a better field, neither removed the need for callers to remember). Now
    /// <see cref="RunAsync"/> returns a genuinely distinct <see cref="ResultFailureType.Forbidden"/>
    /// outcome for a refusal, and a real dispatch outcome — success or a genuine CLI failure — is
    /// always <see cref="Result.IsSuccess"/>. This mapper reads that type-level distinction
    /// directly; a caller can still forget to call it at a new dispatch site, but the distinction it
    /// reads can no longer drift to the wrong field.
    /// </para>
    /// <para>
    /// A dispatch that failed for any other reason (the sandbox threw before returning a result — see
    /// <see cref="RunAsync"/>'s own try/catch around the executor call) maps to
    /// <paramref name="errorCode"/> instead of <paramref name="deniedCode"/>, preserving the
    /// existing three-way split between "refused", "sandbox-level error", and "ran, parse the result".
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The result payload type of the caller's own <see cref="Result{T}"/>.</typeparam>
    /// <param name="dispatch">The outcome of a <see cref="RunAsync"/> call.</param>
    /// <param name="logger">The caller's own logger, used so the log entry attributes to the right category.</param>
    /// <param name="backendLabel">The IaC backend name for the log message (e.g. <c>"Terraform"</c>, <c>"Bicep"</c>).</param>
    /// <param name="operationLabel">The operation for the log message (e.g. <c>"iac_plan"</c>, <c>"iac_scan (checkov)"</c>).</param>
    /// <param name="moduleDirectory">The module directory the run targeted.</param>
    /// <param name="deniedCode">The stable <c>iac.*.sandbox_denied</c> code to return for a governance refusal.</param>
    /// <param name="errorCode">
    /// The stable <c>iac.*.sandbox_error</c> code to return for any other dispatch failure — always a
    /// sandbox-level exception <see cref="RunAsync"/> itself already caught and logged with the full
    /// exception detail, so this branch does not log a second time.
    /// </param>
    /// <returns>A failed <see cref="Result{T}"/> if <paramref name="dispatch"/> failed; otherwise <c>null</c>.</returns>
    public static Result<T>? MapDispatchFailure<T>(
        Result<SandboxExecutionResult> dispatch,
        ILogger logger,
        string backendLabel,
        string operationLabel,
        string moduleDirectory,
        string deniedCode,
        string errorCode)
    {
        if (dispatch.IsSuccess)
        {
            return null;
        }

        if (dispatch.FailureType == ResultFailureType.Forbidden)
        {
            logger.LogError(
                "{Backend} {Operation} for {Module} was refused before dispatch: {Reason}",
                backendLabel, operationLabel, moduleDirectory, string.Join("; ", dispatch.Errors));
            return Result<T>.Fail(deniedCode);
        }

        return Result<T>.Fail(errorCode);
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
