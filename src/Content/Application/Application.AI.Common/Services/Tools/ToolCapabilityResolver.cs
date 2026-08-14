using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Governance;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Governance;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// Default <see cref="IToolCapabilityResolver"/>. Reads a first-party tool's declaration straight off
/// its keyed-DI registration, falls back to the narrow keyword heuristic, and augments with the MCP
/// open-world hint and operator overrides — see the interface's remarks for the exact precedence.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton, like <see cref="ToolBehaviorRegistry"/>: it holds no per-call state and
/// every input it needs (the config snapshot, the behaviour registry, keyed DI) is already safe to read
/// from any scope.
/// </para>
/// <para>
/// <strong>Never probes keyed DI with a name outside the bounded registered-key set supplied to the
/// constructor.</strong> This resolver is called for every tool in an agent's set, including
/// MCP and bundle-owned tools whose published names are not registration keys — a bundle-owned name
/// embeds a per-run bundle id, so that key space is unbounded across a process lifetime.
/// <c>IServiceProvider.GetKeyedService</c> caches an accessor per distinct key it is asked about, even
/// for a key nothing is registered under, in the ROOT container this singleton holds — so probing an
/// unbounded name space there is unbounded, process-lifetime memory growth, not a per-call cost. The
/// bounded key set (built once, at the same place <see cref="ToolCatalog"/>'s is) is what keeps the
/// probe itself bounded.
/// </para>
/// </remarks>
public sealed class ToolCapabilityResolver : IToolCapabilityResolver
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IToolBehaviorRegistry _behaviorRegistry;
    private readonly IOptionsMonitor<GovernanceConfig> _governanceConfig;
    private readonly IReadOnlySet<string> _registeredFirstPartyToolKeys;

    /// <summary>Initializes a new instance of the <see cref="ToolCapabilityResolver"/> class.</summary>
    /// <param name="registeredFirstPartyToolKeys">
    /// The bounded set of keys <see cref="ITool"/> is actually registered under — see this type's
    /// remarks for why probing keyed DI outside this set is unsafe.
    /// </param>
    public ToolCapabilityResolver(
        IServiceProvider serviceProvider,
        IToolBehaviorRegistry behaviorRegistry,
        IOptionsMonitor<GovernanceConfig> governanceConfig,
        IReadOnlySet<string> registeredFirstPartyToolKeys)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(behaviorRegistry);
        ArgumentNullException.ThrowIfNull(governanceConfig);
        ArgumentNullException.ThrowIfNull(registeredFirstPartyToolKeys);

        _serviceProvider = serviceProvider;
        _behaviorRegistry = behaviorRegistry;
        _governanceConfig = governanceConfig;
        _registeredFirstPartyToolKeys = registeredFirstPartyToolKeys;
    }

    /// <inheritdoc />
    public ToolCapabilityProfile Resolve(string publishedToolName)
    {
        if (string.IsNullOrWhiteSpace(publishedToolName))
            return ToolCapabilityProfile.Unclassified(publishedToolName ?? string.Empty);

        var config = _governanceConfig.CurrentValue.ToolCompositionGating;

        // The behaviour registry is keyed on the same published name this method receives (see
        // BehaviorRecordingMcpToolProvider.Record), so it doubles as the source of two facts this
        // resolver needs: which MCP server (if any) advertised the tool, and its open-world hint.
        var behavior = _behaviorRegistry.Resolve(publishedToolName);
        var serverName = behavior.ServerName;

        // 1. Per-tool operator override wins outright, in both directions, when it applies to this
        // tool's actual source. A blank Server on the override applies to any source by that name —
        // safe only because the validator requires Server whenever Capabilities is empty (a clearing
        // override), so a name-only override reaching here can only ever add bits, never remove them.
        var toolOverride = FindApplicableToolOverride(config.ToolCapabilities, publishedToolName, serverName);
        if (toolOverride is not null)
        {
            return new ToolCapabilityProfile(
                publishedToolName, ToOneFlags(toolOverride.Capabilities),
                ToolCapabilityOrigin.OperatorOverride, serverName);
        }

        var (capabilities, origin) = ResolveBase(publishedToolName);

        // 2. MCP open-world annotation adds IngestsUntrustedInput — believed from any source, unlike a
        // loosening readOnlyHint claim, because it only ever adds friction to this check. A hostile
        // server gains nothing by asserting it.
        //
        // Origin is upgraded whenever a STRONGER source contributes, not only when the profile was
        // previously None. A profile is a single record describing possibly-several bits from
        // possibly-several sources, and Origin can only ever report one of them — reporting the
        // strongest source that vouches for the profile, rather than whichever source happened to run
        // first, is what keeps an approver from being shown "keyword guess" for a bit a real server
        // annotation actually backs.
        if (behavior.OpenWorld == true && (capabilities & ToolCompositionCapability.IngestsUntrustedInput) == ToolCompositionCapability.None)
        {
            capabilities |= ToolCompositionCapability.IngestsUntrustedInput;
            origin = StrongerOf(origin, ToolCapabilityOrigin.McpAnnotation);
        }

        // 3. Per-server operator override — additive only. Never applied ahead of the per-tool override
        // above: a per-tool override that clears bits must win over a server-wide addition, or the
        // clearing override would be immediately re-tainted by the server it was written to override.
        // OperatorOverride is the strongest source by construction, so a non-empty server override
        // always wins the Origin label — an operator's explicit, reviewed statement about a server
        // outranks any guess, whether or not it happens to overlap bits a weaker source already found.
        if (serverName is { Length: > 0 })
        {
            var serverOverride = config.ServerCapabilities.FirstOrDefault(
                s => string.Equals(s.Server, serverName, StringComparison.OrdinalIgnoreCase));

            if (serverOverride is { Capabilities.Count: > 0 })
            {
                capabilities |= ToOneFlags(serverOverride.Capabilities);
                origin = ToolCapabilityOrigin.OperatorOverride;
            }
        }

        return capabilities == ToolCompositionCapability.None
            ? ToolCapabilityProfile.Unclassified(publishedToolName)
            : new ToolCapabilityProfile(publishedToolName, capabilities, origin, serverName);
    }

    /// <summary>
    /// The base classification before any annotation or override augments it: a first-party tool's own
    /// declaration, authoritative in both directions, or — only when no first-party registration exists
    /// at all — the narrow keyword heuristic against the published name.
    /// </summary>
    /// <remarks>
    /// <strong>A registered first-party tool's declaration is never overridden by the keyword
    /// heuristic, even when it is the DIM default <see cref="ToolCompositionCapability.None"/>.</strong>
    /// <see cref="ITool.Capabilities"/>'s own remarks document why: leaving a tool undeclared is meant
    /// to surface as a visible unclassified count, not to be silently patched over by a name-based
    /// guess. The keyword heuristic exists for the case a first-party declaration cannot cover at
    /// all — a third-party MCP tool, which has no <see cref="ITool"/> registration to consult.
    /// </remarks>
    private (ToolCompositionCapability Capabilities, ToolCapabilityOrigin Origin) ResolveBase(string publishedToolName)
    {
        // Gated on the bounded registered-key set before ever reaching the container — see this
        // type's remarks. An MCP or bundle-owned name that is not a registration key skips straight to
        // the keyword heuristic below, exactly as it would if GetKeyedService had been called and
        // returned null, but without teaching the root provider a new permanent, never-reused key.
        var firstParty = _registeredFirstPartyToolKeys.Contains(publishedToolName)
            ? _serviceProvider.GetKeyedService<ITool>(publishedToolName)
            : null;
        if (firstParty is not null)
        {
            return firstParty.Capabilities != ToolCompositionCapability.None
                ? (firstParty.Capabilities, ToolCapabilityOrigin.FirstParty)
                : (ToolCompositionCapability.None, ToolCapabilityOrigin.Unclassified);
        }

        var keyword = ToolCapabilityKeywordRules.Classify(publishedToolName);
        return keyword != ToolCompositionCapability.None
            ? (keyword, ToolCapabilityOrigin.KeywordHeuristic)
            : (ToolCompositionCapability.None, ToolCapabilityOrigin.Unclassified);
    }

    /// <summary>
    /// The per-tool override matching rule: the name matches, and either the override names no server
    /// (applies to any source) or names the server this declaration actually came from.
    /// </summary>
    /// <remarks>
    /// <strong>Deliberately not the same rule as <c>ToolInvocationGovernor.ExemptionCoversSource</c>,</strong>
    /// though both exist to stop a tool name — which belongs to nobody — from letting a server-scoped
    /// config entry silently cover a different server's tool of the same name. That rule additionally
    /// requires <c>behavior.IsVouchedFor</c> before honouring a bare-name entry, because a behaviour
    /// exemption can <em>loosen</em> a gate (skip approval for a tool that would otherwise need it).
    /// A bare-name entry here can only ever <em>add</em> capability bits: the validator requires
    /// <c>Server</c> whenever <c>Capabilities</c> is empty, so a clearing override without a named
    /// server never reaches this method at all. Adding bits only makes the composition check more
    /// cautious, so there is no loosening direction here that needs a provenance guard.
    /// </remarks>
    private static ToolCapabilityOverride? FindApplicableToolOverride(
        List<ToolCapabilityOverride> overrides, string toolName, string? serverName) =>
        overrides.FirstOrDefault(entry =>
            string.Equals(entry.Tool, toolName, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(entry.Server)
                || string.Equals(entry.Server, serverName, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Trust ranking used to decide which source's name is reported when more than one has vouched for
    /// a profile. Distinct from declaration order in <see cref="ToolCapabilityOrigin"/> itself, which is
    /// definition order, not strength order.
    /// </summary>
    private static readonly Dictionary<ToolCapabilityOrigin, int> OriginStrength = new()
    {
        [ToolCapabilityOrigin.Unclassified] = 0,
        [ToolCapabilityOrigin.KeywordHeuristic] = 1,
        [ToolCapabilityOrigin.McpAnnotation] = 2,
        [ToolCapabilityOrigin.FirstParty] = 3,
        [ToolCapabilityOrigin.OperatorOverride] = 4,
    };

    private static ToolCapabilityOrigin StrongerOf(ToolCapabilityOrigin a, ToolCapabilityOrigin b) =>
        OriginStrength[b] > OriginStrength[a] ? b : a;

    private static ToolCompositionCapability ToOneFlags(List<ToolCompositionCapability> flags)
    {
        var combined = ToolCompositionCapability.None;
        foreach (var flag in flags)
            combined |= flag;
        return combined;
    }
}
