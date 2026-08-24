using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Tools;
using Domain.AI.Governance;
using Domain.Common;
using Domain.Common.Config.AI;
using Domain.Common.Helpers;
using Domain.AI.Sandbox;
using Domain.Common.Config.AI.Sandbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.Services.Sandbox;

/// <summary>
/// Resolves the effective <see cref="ToolPermissionProfile"/> for a tool by merging its
/// <see cref="ITool.RequiredCapabilities"/>/<see cref="ITool.MinimumIsolation"/> declaration with
/// runtime <see cref="ToolOverrideConfig"/> from appsettings. Uses deny-overrides-allow semantics.
/// </summary>
/// <remarks>
/// Mirrors <see cref="ToolCapabilityResolver"/> (the sibling resolver for the tool-composition
/// capability model): the base classification comes from the shared bounded-key-set-gated
/// <see cref="FirstPartyToolLookup"/>, not from a separately-populated cache a caller has to
/// remember to feed. The previous design read a <c>[ToolCapabilityAttribute]</c> cached via an
/// explicit <c>RegisterToolType</c> call — nothing in production ever called it, so every tool
/// resolved <see cref="ToolCapability.None"/> regardless of what it actually needed, and the
/// capability check downstream (<c>CapabilityEnforcer</c>) could never refuse a call (#387).
/// </remarks>
public sealed class ToolPermissionProfileResolver
{
    private readonly FirstPartyToolLookup _firstPartyLookup;
    private readonly IOptionsMonitor<SandboxConfig> _config;
    private readonly IGovernanceAuditService? _auditService;
    private readonly IOptionsMonitor<GovernanceConfig>? _governanceConfig;

    /// <summary>Initializes a new instance of the <see cref="ToolPermissionProfileResolver"/> class.</summary>
    /// <param name="firstPartyLookup">
    /// The shared bounded-key-set-gated first-party tool lookup — see its remarks for why probing
    /// keyed DI outside its bounded key set is unsafe.
    /// </param>
    /// <param name="config">Sandbox configuration with per-tool overrides.</param>
    /// <param name="auditService">
    /// Optional durable audit sink for a refusal on the ungoverned-dispatch path (#419) — every
    /// governed refusal already reaches <c>governance.jsonl</c> via <c>CapabilityEnforcer</c>/
    /// <c>ToolInvocationGovernor</c>'s use of the same interface; a refusal from
    /// <see cref="ResolveForUngovernedDispatch"/> previously reached neither that trail nor an app
    /// log (the app-log gap was closed separately, per-caller, in #421/#426). Resolved optionally —
    /// mirroring <c>ProvenanceMemoryWriteGate</c>'s <c>IGovernanceAuditService?</c> convention — rather
    /// than required, so a composition root that never calls <c>AddGovernance</c> still constructs
    /// this widely-used singleton; it just gets no durable audit trail for this path.
    /// </param>
    /// <param name="governanceConfig">
    /// Gates <paramref name="auditService"/>'s <c>.Log(...)</c> calls on <c>GovernanceConfig.EnableAudit</c>
    /// (#419 code-review finding) — every other audit call site in the codebase honors this same
    /// toggle (<c>ToolInvocationGovernor</c>, <c>PromptInjectionBehavior</c>), so an operator who sets
    /// it <see langword="false"/> must not keep seeing writes to <c>governance.jsonl</c> from this
    /// path alone. Optional for the same reason <paramref name="auditService"/> is: when absent,
    /// <see cref="GovernanceConfig.EnableAudit"/>'s own default (<see langword="true"/>) applies, so a
    /// composition root that doesn't wire this still gets the historically-correct "audit on" behavior
    /// rather than silently losing the trail.
    /// </param>
    public ToolPermissionProfileResolver(
        FirstPartyToolLookup firstPartyLookup,
        IOptionsMonitor<SandboxConfig> config,
        IGovernanceAuditService? auditService = null,
        IOptionsMonitor<GovernanceConfig>? governanceConfig = null)
    {
        ArgumentNullException.ThrowIfNull(firstPartyLookup);
        ArgumentNullException.ThrowIfNull(config);

        _firstPartyLookup = firstPartyLookup;
        _config = config;
        _auditService = auditService;
        _governanceConfig = governanceConfig;
    }

    /// <summary>
    /// Resolves the effective permission profile by merging the tool's own declaration
    /// (<see cref="ITool.RequiredCapabilities"/>/<see cref="ITool.MinimumIsolation"/>) with runtime
    /// configuration overrides.
    /// </summary>
    /// <param name="toolName">The keyed DI tool name.</param>
    /// <returns>The merged permission profile.</returns>
    public ToolPermissionProfile Resolve(string toolName)
    {
        var (baseCapabilities, baseIsolation) = ResolveBase(toolName);
        var (deniedCaps, effectiveIsolation) = ResolveOverride(toolName, baseIsolation);

        return new ToolPermissionProfile
        {
            // The tool's undiminished declaration — never folded with the deny list (#405). See
            // ToolPermissionProfile.EffectiveCapabilities for the value consumers should read.
            RequiredCapabilities = baseCapabilities,
            DeniedCapabilities = deniedCaps,
            MinimumIsolation = effectiveIsolation
        };
    }

    /// <summary>
    /// Parses <paramref name="toolName"/>'s <see cref="ToolOverrideConfig"/>, if any, into a
    /// <c>DeniedCapabilities</c> value and an isolation floor merged with <paramref name="baseIsolation"/>
    /// — the override-parsing logic shared by <see cref="Resolve"/> and
    /// <see cref="ResolveForUngovernedDispatch"/>, factored out so the latter no longer needs a second
    /// <see cref="FirstPartyToolLookup.Resolve"/> call just to reach this (a security-review finding:
    /// real keyed-DI resolution, not a dictionary read, so the duplicate lookup was a real, if small,
    /// per-call cost on a hot dispatch path).
    /// </summary>
    private (ToolCapability DeniedCapabilities, SandboxIsolationLevel EffectiveIsolation) ResolveOverride(
        string toolName, SandboxIsolationLevel baseIsolation)
    {
        _config.CurrentValue.ToolOverrides.TryGetValue(toolName, out var overrideConfig);
        if (overrideConfig is null)
            return (ToolCapability.None, baseIsolation);

        var deniedCaps = ParseCapabilities(overrideConfig.DeniedCapabilities);

        var overrideIsolation = EnumNameHelper.TryParseName<SandboxIsolationLevel>(
            overrideConfig.MinimumIsolation, out var parsed)
            ? parsed
            : SandboxIsolationLevel.None;
        var effectiveIsolation = baseIsolation.AtLeast(overrideIsolation);

        return (deniedCaps, effectiveIsolation);
    }

    /// <summary>
    /// Resolves a permission profile for a caller that dispatches directly to an
    /// <c>ISandboxExecutor</c> without going through <c>ICapabilityEnforcer</c>/
    /// <c>IToolInvocationGovernor</c> — <c>WorkspaceCommandRunner</c> and <c>IacSandboxRunner</c>,
    /// which are not governed tool calls, so nothing else on their path ever checks an operator's
    /// <c>DeniedCapabilities</c> against <paramref name="requiredCapabilities"/> the way
    /// <c>CapabilityEnforcer.EnforceAsync</c> does for every governed call (#405).
    /// </summary>
    /// <param name="toolName">The keyed DI tool name, used to look up the operator's override.</param>
    /// <param name="requiredCapabilities">
    /// The caller-supplied capabilities this run needs — kept as the caller's own declaration rather
    /// than re-derived from the base lookup, so a caller whose name is outside the bounded first-party
    /// key set (or whose declaration legitimately differs per call) isn't second-guessed.
    /// </param>
    /// <param name="allowedPrograms">The runtime-derived allowed-programs list for this run.</param>
    /// <param name="defaultIsolationLevel">
    /// The caller's own minimum isolation requirement, independent of any operator override — the
    /// floor the returned profile's <see cref="ToolPermissionProfile.MinimumIsolation"/> never drops
    /// below. Defaults to <see cref="SandboxIsolationLevel.Process"/>, since this dispatch path
    /// requires at least process isolation even absent an explicit floor.
    /// </param>
    /// <returns>
    /// A forbidden <see cref="Result{T}"/> when the deny override intersects
    /// <paramref name="requiredCapabilities"/> — refusing outright, the same as a governed call, rather
    /// than silently narrowing what gets provisioned. Also forbidden when <paramref name="toolName"/>
    /// is a registered first-party tool and <paramref name="requiredCapabilities"/> under-declares
    /// relative to that tool's own <see cref="ITool.RequiredCapabilities"/> — see the under-declaration
    /// remarks below. Otherwise the merged profile: the override's <c>DeniedCapabilities</c> carried
    /// through (for <see cref="ToolPermissionProfile.EffectiveCapabilities"/>), and isolation set to
    /// <paramref name="defaultIsolationLevel"/><c>.AtLeast(operatorOverride)</c> — never downgraded
    /// below the caller's own floor even when no override is configured.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Under-declaration check (#405 follow-up, a security-review finding):</strong>
    /// <paramref name="requiredCapabilities"/> is the caller's own, separately-maintained
    /// declaration — <c>WorkspaceRunTestsTool.RequiredSandboxCapabilities</c> and its siblings — not
    /// re-derived from <see cref="ResolveBase"/> the way <see cref="Resolve"/> does for a governed
    /// call. Nothing previously stopped that declaration from silently drifting under the tool's own
    /// registered <c>ITool.RequiredCapabilities</c> (a copy-paste error, or one constant updated and
    /// the other forgotten) — this dispatch path bypasses <c>CapabilityEnforcer</c> entirely, so
    /// under-declaring here means the sandbox is provisioned with less than the tool's own
    /// registration says it needs, with no other check positioned to catch it. Refuses outright
    /// rather than silently widening to the registered declaration, matching this method's existing
    /// deny-intersection posture: a mismatch is a configuration defect to surface, not one to paper
    /// over by picking a value for the caller.
    /// </para>
    /// <para>
    /// <strong><paramref name="defaultIsolationLevel"/> (security-review finding, #405 follow-up):</strong>
    /// this method used to hardcode the returned floor to <see cref="SandboxIsolationLevel.Process"/>
    /// regardless of what the caller actually required, so a consumer constructed with an elevated
    /// floor (the constructor parameter every first-party caller of this dispatch path exposes) got
    /// the right sandbox executor selected — the caller computes
    /// <c>defaultIsolationLevel.AtLeast(profile.MinimumIsolation)</c> for that — but a profile whose
    /// own <see cref="ToolPermissionProfile.MinimumIsolation"/> still read the un-elevated value.
    /// <c>SandboxSessionAttestationSigner</c>'s <c>capabilitiesEnforcedBy</c> field read that field, so
    /// a caller with an elevated floor got the correct executor but a signed record based on the wrong
    /// tier. Unreachable today — every shipped call site's floor defaults to
    /// <see cref="SandboxIsolationLevel.Process"/> — but latent for the first consumer that isn't.
    /// (<c>DockerSandboxExecutor</c> used to have an equivalent Docker-unavailable fallback gate keyed
    /// off this same field; #434 removed it — that class's Docker-unavailable path no longer branches
    /// on <see cref="ToolPermissionProfile.MinimumIsolation"/> at all, so this specific staleness risk
    /// no longer applies there.) Now that this method receives the floor directly, the caller no longer needs its
    /// own outer <see cref="SandboxIsolationLevelExtensions.AtLeast"/> call against the returned
    /// profile — <see cref="ToolPermissionProfile.MinimumIsolation"/> already reflects it.
    /// </para>
    /// </remarks>
    /// <param name="agentId">
    /// The dispatching agent's identifier, recorded on a refusal's audit entry (#419) — <c>null</c>
    /// when the caller has no agent identity to supply (e.g. direct unit-test calls), in which case
    /// the audit entry records <c>"unknown"</c> rather than omitting the entry.
    /// </param>
    public Result<ToolPermissionProfile> ResolveForUngovernedDispatch(
        string toolName,
        ToolCapability requiredCapabilities,
        IReadOnlyList<string> allowedPrograms,
        SandboxIsolationLevel defaultIsolationLevel = SandboxIsolationLevel.Process,
        string? agentId = null)
    {
        // Single FirstPartyToolLookup.Resolve call, reused for the under-declaration check below and
        // as ResolveOverride's isolation floor — this used to call Resolve(toolName) (itself a lookup,
        // via ResolveBase) AND a second direct lookup just for RequiredCapabilities (a security-review
        // finding: real keyed-DI resolution, not a dictionary read, paid twice on this dispatch path).
        var firstPartyTool = _firstPartyLookup.Resolve(toolName);

        if (firstPartyTool is not null)
        {
            var underDeclared = firstPartyTool.RequiredCapabilities & ~requiredCapabilities;
            if (underDeclared != ToolCapability.None)
            {
                LogRefusal(agentId, toolName);
                return Result<ToolPermissionProfile>.Forbidden(
                    $"Tool '{toolName}' was dispatched with capabilities narrower than its own " +
                    $"registered declaration: missing {underDeclared}.");
            }
        }

        var baseIsolation = firstPartyTool?.MinimumIsolation ?? SandboxIsolationLevel.None;
        var (deniedCaps, overrideIsolation) = ResolveOverride(toolName, baseIsolation);

        var denied = requiredCapabilities & deniedCaps;
        if (denied != ToolCapability.None)
        {
            LogRefusal(agentId, toolName);
            return Result<ToolPermissionProfile>.Forbidden(
                $"Tool '{toolName}' requires capabilities denied by operator override: {denied}");
        }

        return Result<ToolPermissionProfile>.Success(new ToolPermissionProfile
        {
            RequiredCapabilities = requiredCapabilities,
            DeniedCapabilities = deniedCaps,
            AllowedPrograms = allowedPrograms,
            MinimumIsolation = defaultIsolationLevel.AtLeast(overrideIsolation)
        });
    }

    /// <summary>
    /// Writes a refusal to the durable audit trail — gated on <see cref="GovernanceConfig.EnableAudit"/>
    /// (#419 code-review finding), matching every other <see cref="IGovernanceAuditService"/> call site
    /// in the codebase via the shared <see cref="GovernanceAuditGateExtensions.LogIfAuditEnabled(IGovernanceAuditService?, IOptionsMonitor{GovernanceConfig}?, string?, string, string)"/>
    /// gate (#430). An operator who sets that flag <see langword="false"/> expects every tamper-evident
    /// write to stop, not just the governed ones — a single unconditional call site here would silently
    /// break that contract.
    /// </summary>
    private void LogRefusal(string? agentId, string toolName) =>
        _auditService.LogIfAuditEnabled(_governanceConfig, agentId, toolName, ToolDecisionOutcome.Denied.ToString());

    /// <summary>
    /// As <see cref="ResolveForUngovernedDispatch"/>, but also resolves the keyed-scoped
    /// <see cref="ISandboxExecutor"/> for the profile's resolved
    /// <see cref="ToolPermissionProfile.MinimumIsolation"/> tier — the profile-then-executor ordering
    /// invariant folded into this method's own implementation, not left for each caller to reproduce
    /// (a /simplify finding on the #405 follow-up: <c>WorkspaceCommandRunner.RunAsync</c> and
    /// <c>IacSandboxRunner.RunAsync</c> each independently resolved the profile, then separately
    /// resolved the executor from it, with the ordering explained only in a comment repeated at both
    /// call sites — nothing but that prose stopped a future edit at either site from resolving the
    /// executor before the profile again, which is exactly the stale-tier bug this same follow-up
    /// fixed once already).
    /// </summary>
    /// <param name="scopedServices">
    /// The per-execution DI scope's provider, used to resolve the keyed-scoped
    /// <see cref="ISandboxExecutor"/> only after the profile — never a caller-supplied, already-resolved
    /// executor, which is exactly what made the ordering violable in the first place.
    /// </param>
    /// <returns>
    /// The same forbidden <see cref="Result{T}"/> cases as <see cref="ResolveForUngovernedDispatch"/>,
    /// or the resolved profile paired with the executor selected for its
    /// <see cref="ToolPermissionProfile.MinimumIsolation"/>.
    /// </returns>
    public Result<(ToolPermissionProfile Profile, ISandboxExecutor Executor)> ResolveExecutorForUngovernedDispatch(
        string toolName,
        ToolCapability requiredCapabilities,
        IReadOnlyList<string> allowedPrograms,
        IServiceProvider scopedServices,
        SandboxIsolationLevel defaultIsolationLevel = SandboxIsolationLevel.Process)
    {
        ArgumentNullException.ThrowIfNull(scopedServices);

        // IAgentExecutionContext is scoped (never captured on this singleton's own constructor —
        // that would be a captive dependency pinning every call to whichever scope first resolved
        // this singleton) so it is read from the caller's per-execution scope here, at the one place
        // this dispatch path already receives one, and forwarded down for the audit entry (#419).
        var agentId = scopedServices.GetService<IAgentExecutionContext>()?.AgentId;

        var profileResult = ResolveForUngovernedDispatch(
            toolName, requiredCapabilities, allowedPrograms, defaultIsolationLevel, agentId);
        if (!profileResult.IsSuccess)
            return Result<(ToolPermissionProfile, ISandboxExecutor)>.Forbidden(
                string.Join("; ", profileResult.Errors));

        var executor = scopedServices.GetRequiredKeyedService<ISandboxExecutor>(
            profileResult.Value!.MinimumIsolation);
        return Result<(ToolPermissionProfile, ISandboxExecutor)>.Success((profileResult.Value!, executor));
    }

    /// <summary>
    /// The base declaration before any override: a registered first-party tool's own
    /// <see cref="ITool.RequiredCapabilities"/>/<see cref="ITool.MinimumIsolation"/>, or
    /// <see cref="ToolCapability.None"/>/<see cref="SandboxIsolationLevel.None"/> for a name outside
    /// the bounded registered-key set (MCP or bundle-owned tools — never covered by capability
    /// declarations either way).
    /// </summary>
    private (ToolCapability Capabilities, SandboxIsolationLevel Isolation) ResolveBase(string toolName)
    {
        var firstParty = _firstPartyLookup.Resolve(toolName);

        return firstParty is not null
            ? (firstParty.RequiredCapabilities, firstParty.MinimumIsolation)
            : (ToolCapability.None, SandboxIsolationLevel.None);
    }

    /// <summary>
    /// Parses capability names (e.g., "FileRead", "NetworkAccess") into a combined
    /// <see cref="ToolCapability"/> flags value. Unrecognised names are ignored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Names only — but a comma-separated entry is still a list of names.</strong> Numeric
    /// forms are refused: <c>Enum.TryParse&lt;ToolCapability&gt;("255", …)</c> succeeds and sets
    /// every bit including undefined ones, which on the granting side
    /// (<c>SandboxConfig.DefaultGrantedCapabilities</c>, read by <c>ToolInvocationGovernor</c>) hands
    /// a tool every capability the sandbox model has.
    /// </para>
    /// <para>
    /// A comma inside one entry is split and each token parsed by name, rather than rejected. The
    /// distinction matters because this method also feeds a <em>deny</em> list
    /// (<c>ToolOverrideConfig.DeniedCapabilities</c>), where dropping an entry fails <em>open</em>:
    /// the capability stays granted, and <c>ToolPermissionProfile.EffectiveCapabilities</c> — read
    /// by <c>DockerContainerLaunchPreparer</c> to decide container network access and whether the
    /// bind mount is read-only, and by <c>CapabilityEnforcer</c> to decide what to grant — resolves
    /// as if the deny were never written. Refusing <c>"NetworkAccess,FileWrite"</c> outright would
    /// silently convert a working deny into a live grant on upgrade. Splitting keeps every name the
    /// operator wrote meaningful while still refusing the numeric form, which is the shape that
    /// actually loses information.
    /// </para>
    /// </remarks>
    public static ToolCapability ParseCapabilities(IEnumerable<string> names)
    {
        var result = ToolCapability.None;
        foreach (var entry in names)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;

            foreach (var token in entry.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (EnumNameHelper.TryParseName<ToolCapability>(token, out var cap))
                    result |= cap;
            }
        }
        return result;
    }
}
