using Application.AI.Common.Interfaces.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// Bounded-key-set-gated first-party <see cref="ITool"/> lookup — the single place
/// <see cref="ToolCapabilityResolver"/>, <c>ToolPermissionProfileResolver</c>, and
/// <see cref="ToolRiskClassifier"/> resolve a tool's own declaration from keyed DI. Each answers a
/// different question about the same tool (data-flow risk, sandbox capabilities, graded-autonomy
/// blast radius), but all three need the identical bounded-lookup safety invariant, so it lives here
/// once rather than in independently-maintained copies (#387 follow-up: found duplicated — twice —
/// during code review).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Never probes keyed DI with a name outside the bounded registered-key set supplied to the
/// constructor.</strong> Every resolver this feeds is called for every tool in an agent's set,
/// including MCP and bundle-owned tools whose published names are not registration keys — a
/// bundle-owned name embeds a per-run bundle id, so that key space is unbounded across a process
/// lifetime. <c>IServiceProvider.GetKeyedService</c> caches an accessor per distinct key it is asked
/// about, even for a key nothing is registered under, in the ROOT container this type holds — so
/// probing an unbounded name space there is unbounded, process-lifetime memory growth, not a per-call
/// cost. The bounded key set (built once, at the same place <c>IToolCatalog</c>'s is) is what keeps
/// the probe itself bounded.
/// </para>
/// </remarks>
public sealed class FirstPartyToolLookup
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IReadOnlySet<string> _registeredFirstPartyToolKeys;

    /// <summary>Initializes a new instance of the <see cref="FirstPartyToolLookup"/> class.</summary>
    /// <param name="serviceProvider">Root service provider, for bounded keyed-DI lookup.</param>
    /// <param name="registeredFirstPartyToolKeys">
    /// The bounded set of keys <see cref="ITool"/> is actually registered under — see this type's
    /// remarks for why probing keyed DI outside this set is unsafe.
    /// </param>
    public FirstPartyToolLookup(
        IServiceProvider serviceProvider,
        IReadOnlySet<string> registeredFirstPartyToolKeys)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(registeredFirstPartyToolKeys);

        _serviceProvider = serviceProvider;
        _registeredFirstPartyToolKeys = registeredFirstPartyToolKeys;
    }

    /// <summary>
    /// Resolves the first-party <see cref="ITool"/> registered under <paramref name="toolName"/>, or
    /// <see langword="null"/> when the name is outside the bounded key set or the keyed registration
    /// itself resolves to null.
    /// </summary>
    /// <param name="toolName">The tool's published name.</param>
    public ITool? Resolve(string toolName) =>
        _registeredFirstPartyToolKeys.Contains(toolName)
            ? _serviceProvider.GetKeyedService<ITool>(toolName)
            : null;
}
