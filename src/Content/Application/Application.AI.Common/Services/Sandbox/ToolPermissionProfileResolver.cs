using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Tools;
using Domain.Common;
using Domain.Common.Helpers;
using Domain.AI.Sandbox;
using Domain.Common.Config.AI.Sandbox;
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

    /// <summary>Initializes a new instance of the <see cref="ToolPermissionProfileResolver"/> class.</summary>
    /// <param name="firstPartyLookup">
    /// The shared bounded-key-set-gated first-party tool lookup — see its remarks for why probing
    /// keyed DI outside its bounded key set is unsafe.
    /// </param>
    /// <param name="config">Sandbox configuration with per-tool overrides.</param>
    public ToolPermissionProfileResolver(
        FirstPartyToolLookup firstPartyLookup,
        IOptionsMonitor<SandboxConfig> config)
    {
        ArgumentNullException.ThrowIfNull(firstPartyLookup);
        ArgumentNullException.ThrowIfNull(config);

        _firstPartyLookup = firstPartyLookup;
        _config = config;
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

        _config.CurrentValue.ToolOverrides.TryGetValue(toolName, out var overrideConfig);

        if (overrideConfig is null)
        {
            return new ToolPermissionProfile
            {
                RequiredCapabilities = baseCapabilities,
                MinimumIsolation = baseIsolation
            };
        }

        var deniedCaps = ParseCapabilities(overrideConfig.DeniedCapabilities);

        var overrideIsolation = EnumNameHelper.TryParseName<SandboxIsolationLevel>(
            overrideConfig.MinimumIsolation, out var parsed)
            ? parsed
            : SandboxIsolationLevel.None;
        var effectiveIsolation = (SandboxIsolationLevel)Math.Max(
            (int)baseIsolation, (int)overrideIsolation);

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
    /// <returns>
    /// A forbidden <see cref="Result{T}"/> when the deny override intersects
    /// <paramref name="requiredCapabilities"/> — refusing outright, the same as a governed call, rather
    /// than silently narrowing what gets provisioned. Otherwise the merged profile: the override's
    /// <c>DeniedCapabilities</c> carried through (for <see cref="ToolPermissionProfile.EffectiveCapabilities"/>),
    /// and isolation floored at <see cref="SandboxIsolationLevel.Process"/> — never downgraded even
    /// when no override is configured (this dispatch path requires at least process isolation).
    /// </returns>
    public Result<ToolPermissionProfile> ResolveForUngovernedDispatch(
        string toolName,
        ToolCapability requiredCapabilities,
        IReadOnlyList<string> allowedPrograms)
    {
        var overridden = Resolve(toolName);

        var denied = requiredCapabilities & overridden.DeniedCapabilities;
        if (denied != ToolCapability.None)
        {
            return Result<ToolPermissionProfile>.Forbidden(
                $"Tool '{toolName}' requires capabilities denied by operator override: {denied}");
        }

        return Result<ToolPermissionProfile>.Success(new ToolPermissionProfile
        {
            RequiredCapabilities = requiredCapabilities,
            DeniedCapabilities = overridden.DeniedCapabilities,
            AllowedPrograms = allowedPrograms,
            MinimumIsolation = (SandboxIsolationLevel)Math.Max(
                (int)SandboxIsolationLevel.Process, (int)overridden.MinimumIsolation)
        });
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
