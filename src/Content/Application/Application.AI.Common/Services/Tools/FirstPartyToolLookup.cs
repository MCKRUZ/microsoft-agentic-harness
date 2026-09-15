using Application.AI.Common.Interfaces.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
/// <para>
/// <strong>Case-insensitive membership, but always probes DI with the canonical (as-registered)
/// casing (#655).</strong> An operator-authored grant/deny entry can differ from the actual DI
/// registration key only in casing (e.g. a grant of <c>"BASH"</c> against a key registered as
/// <c>"bash"</c>) — every consumer of this type (<c>CapabilityEnvelopeGrantResolver</c>,
/// <c>PluginPermissionRuleProvider</c>) already compares names case-insensitively downstream, so a
/// case-sensitive <c>Contains</c> here silently reproduced the exact "legitimately-intended grant
/// never applies" defect those consumers exist to close, just for casing drift instead of a
/// key/published-name split. Widening only the membership check would not be enough on its own:
/// <c>GetKeyedService</c> resolves by EXACT key regardless of what comparer a caller's own membership
/// check uses, so a case-variant probe that passed a widened <c>Contains</c> would still miss at
/// <c>GetKeyedService</c> — and, worse, would resurface the unbounded-probe memory-growth risk this
/// type's own remarks above warn about, since a caller could then coin arbitrarily many
/// case-variant strings that all pass membership but each cache their own miss in the root container.
/// A single case-insensitive <see cref="HashSet{T}"/> closes both problems together (/simplify
/// finding: an earlier version of this fix kept a separate case-sensitive set alongside a second,
/// purpose-built casing-lookup dictionary — <see cref="HashSet{T}.TryGetValue(T,out T)"/> already
/// recovers the canonical stored form of an equal-under-the-comparer value, so one collection does
/// both jobs). <see cref="Resolve"/> always probes DI with the canonical casing
/// <see cref="HashSet{T}.TryGetValue(T,out T)"/> recovers, never whatever casing the caller supplied.
/// </para>
/// </remarks>
public sealed class FirstPartyToolLookup
{
    private readonly IServiceProvider _serviceProvider;
    private readonly HashSet<string> _registeredFirstPartyToolKeys;

    /// <summary>Initializes a new instance of the <see cref="FirstPartyToolLookup"/> class.</summary>
    /// <param name="serviceProvider">Root service provider, for bounded keyed-DI lookup.</param>
    /// <param name="registeredFirstPartyToolKeys">
    /// The bounded set of keys <see cref="ITool"/> is actually registered under — see this type's
    /// remarks for why probing keyed DI outside this set is unsafe.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Two entries in <paramref name="registeredFirstPartyToolKeys"/> differ only by case — a
    /// first-party registration bug, not an operator-configuration problem, so this fails loudly at
    /// construction rather than resolving one of the two arbitrarily forever. This type is registered
    /// as a singleton built from a factory delegate (<c>Application.AI.Common.DependencyInjection</c>),
    /// and every production host builds its container with <c>ValidateOnBuild = true</c> (either via
    /// <c>IServiceCollectionExtensions.BuildValidatedServiceProvider</c> for the console-style hosts,
    /// or <c>UseDefaultServiceProvider</c> for the ASP.NET Core ones), which eagerly constructs every
    /// registered singleton — so a real collision genuinely surfaces as a boot failure in every host,
    /// not merely a mid-request one, without needing this type to know anything about that policy
    /// itself.
    /// </exception>
    public FirstPartyToolLookup(
        IServiceProvider serviceProvider,
        IReadOnlySet<string> registeredFirstPartyToolKeys)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(registeredFirstPartyToolKeys);

        _serviceProvider = serviceProvider;
        _registeredFirstPartyToolKeys = BuildCaseInsensitiveKeySet(registeredFirstPartyToolKeys);
    }

    /// <summary>
    /// <see cref="HashSet{T}"/>'s own constructor overload taking an <see cref="IEqualityComparer{T}"/>
    /// does NOT throw on a collision under that comparer — it silently keeps whichever entry it saw
    /// first (standard set-union semantics) — so the fail-loud collision check still needs its own
    /// explicit loop rather than being a side effect of construction.
    /// </summary>
    private static HashSet<string> BuildCaseInsensitiveKeySet(IReadOnlySet<string> canonicalKeys)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in canonicalKeys)
        {
            if (!keys.Add(key))
            {
                keys.TryGetValue(key, out var existing);
                throw new InvalidOperationException(
                    $"Two first-party tool registration keys differ only by case: '{existing}' and " +
                    $"'{key}'. Keyed DI resolves by exact key, so these are two independent " +
                    "registrations that would collide under this type's case-insensitive lookup — " +
                    "rename one to a distinct key.");
            }
        }

        return keys;
    }

    /// <summary>
    /// Resolves the first-party <see cref="ITool"/> registered under <paramref name="toolName"/>, or
    /// <see langword="null"/> when the name is outside the bounded key set or the keyed registration
    /// itself resolves to null.
    /// </summary>
    /// <remarks>
    /// Private deliberately (#627 code-review): this propagates a keyed tool's constructor exception
    /// instead of catching it, which is exactly the footgun #627 closed out the last five production
    /// callers of. Keeping it private, with <see cref="TryResolve"/>/<see cref="TryResolveLogged"/> as
    /// the only way in or out of the class, makes that bug class structurally impossible to
    /// reintroduce rather than relying on every future caller remembering the safe overload.
    /// <para>
    /// Looks up <paramref name="toolName"/> case-insensitively against the bounded key set and probes
    /// <c>GetKeyedService</c> with the CANONICAL casing <see cref="HashSet{T}.TryGetValue(T,out T)"/>
    /// recovers, never <paramref name="toolName"/> itself (#655) — see this type's class remarks for
    /// why a case-variant probe needs normalization at this exact point, not just a wider membership
    /// check.
    /// </para>
    /// </remarks>
    /// <param name="toolName">The tool's published name.</param>
    private ITool? Resolve(string toolName) =>
        _registeredFirstPartyToolKeys.TryGetValue(toolName, out var canonicalKey)
            ? _serviceProvider.GetKeyedService<ITool>(canonicalKey)
            : null;

    /// <summary>
    /// Same bounded resolution as the private <c>Resolve</c>, but catches and reports a construction
    /// failure instead of propagating it.
    /// </summary>
    /// <remarks>
    /// A keyed tool's constructor can require a dependency this particular host never wired — the
    /// exact failure mode that broke host boot when an earlier existence-check attempt (#524)
    /// unconditionally constructed every registered tool. Prefer <see cref="TryResolveLogged"/> over
    /// this overload directly when the caller can log the failure through an <see cref="ILogger"/> —
    /// it owns the "resolve, then log on failure" shape once instead of each caller repeating it
    /// (#627 code-review: found independently duplicated across five call sites).
    /// </remarks>
    /// <param name="toolName">The tool's registration key.</param>
    /// <param name="constructionError">
    /// The exception thrown while constructing the tool, or <see langword="null"/> when the name is
    /// outside the bounded key set (not a failure — just an unknown/non-first-party name) or
    /// resolution succeeded.
    /// </param>
    public ITool? TryResolve(string toolName, out Exception? constructionError)
    {
        constructionError = null;

        try
        {
            return Resolve(toolName);
        }
        catch (Exception ex)
        {
            constructionError = ex;
            return null;
        }
    }

    /// <summary>
    /// As <see cref="TryResolve"/>, but also logs a construction failure — the "resolve, then log if
    /// it threw" shape (#627 code-review) previously repeated by hand at every call site instead of
    /// living once here.
    /// </summary>
    /// <param name="toolName">The tool's registration key.</param>
    /// <param name="logger">The caller's own logger, so the error is attributed to the right category.</param>
    /// <param name="context">
    /// A caller-specific phrase completing "Could not construct first-party tool '{toolName}' …" —
    /// e.g. "to classify its risk; falling back to the conservative default". Should name both what
    /// the caller was trying to do and what it does instead, since the fallback itself is silent.
    /// </param>
    /// <returns>The resolved tool, or <see langword="null"/> when unresolved for any reason.</returns>
    public ITool? TryResolveLogged(string toolName, ILogger logger, string context)
    {
        var tool = TryResolve(toolName, out var constructionError);

        if (tool is null && constructionError is not null)
        {
            logger.LogError(constructionError,
                "Could not construct first-party tool '{ToolName}' {Context}.", toolName, context);
        }

        return tool;
    }

    /// <summary>
    /// Every first-party tool name registered under keyed DI — the same bounded set
    /// <see cref="Resolve"/> checks membership against, exposed for a caller that needs to enumerate
    /// rather than look up one name (#524 round-2 code-review: <c>PluginPermissionRuleProvider</c>'s
    /// fail-closed response to an unverified plugin boundary needs every name to deny, not one).
    /// </summary>
    public IReadOnlySet<string> RegisteredFirstPartyToolKeys => _registeredFirstPartyToolKeys;

    /// <summary>
    /// Resolves <paramref name="toolKey"/>'s converted, self-reported <see cref="ITool.Name"/> — the
    /// value a permission resolver actually matches a rule's pattern against at invocation, which can
    /// legitimately disagree with a DI registration key a manifest (a plugin's <c>DeniedTools</c>, a
    /// capability envelope's grant, a bundle's declared tools) names it by. Returns
    /// <see langword="false"/>, with <paramref name="publishedName"/> set to <paramref name="toolKey"/>
    /// itself, when the key names no known first-party tool (an MCP tool name, for which no
    /// first-party resolution is possible or needed) OR constructing it throws.
    /// </summary>
    /// <remarks>
    /// #626 code-review: originally duplicated near-verbatim between <c>PluginPermissionRuleProvider</c>
    /// (#612) and <c>EnvelopePermissionRuleProvider</c> (#626) — exactly the anti-pattern this type's
    /// own class remarks say it exists to prevent (#387: "found duplicated — twice"). Deliberately pure
    /// (no logging): a construction failure is a caller-specific concern (each rule provider names a
    /// different kind of manifest entry in its own log message), so each caller still wraps this with
    /// its own one-line log-on-failure — only the resolve-or-fall-back-to-key logic itself is shared.
    /// </remarks>
    public bool TryResolvePublishedName(string toolKey, out string publishedName, out Exception? constructionError)
    {
        var tool = TryResolve(toolKey, out constructionError);
        if (tool is not null)
        {
            publishedName = tool.Name;
            return true;
        }

        publishedName = toolKey;
        return false;
    }
}
