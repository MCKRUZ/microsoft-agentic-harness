using System.Collections.Concurrent;
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

    // #651: memoizes a SUCCESSFUL key -> published-name resolution for the process lifetime.
    //
    // Why no invalidation is needed — the mapping is immutable once observed. Two facts carry that,
    // and both are stated as the negative/structural claims they are rather than as a count, because a
    // count is the part a future maintainer will re-measure over a different scope and mistrust:
    //   * NO AddKeyedScoped<ITool> or AddKeyedTransient<ITool> registration exists anywhere in the
    //     repo — every first-party tool is a keyed SINGLETON, so DI hands back one instance per key
    //     for the life of the process. (The tools' own registration doc, and #521's entry in
    //     CLAUDE.md's Common Mistakes, both say a keyed tool must stay singleton even when it needs
    //     per-request state, so this is an enforced convention rather than a coincidence of today's
    //     registrations.)
    //   * Every ITool.Name implementation is expression-bodied over a const or a string literal —
    //     including the one worth suspecting, ConnectorToolAdapter.Name => _connector.ToolName, where
    //     each connector hard-codes the literal. None is settable, init-set, or derived from
    //     configuration that could hot-reload.
    // A value that cannot change needs no version key, no ambient-scope key, and no expiry: this is why
    // the memo belongs HERE and not in a permission-rule provider, which would need its own cache with
    // its own separately-argued invalidation rule (PluginPermissionRuleProvider already carries one;
    // EnvelopePermissionRuleProvider was about to grow a second, which is what #651 asked for).
    //
    // ONLY successes are memoized, and that is a safety property, not an optimization detail:
    //   * A name OUTSIDE the bounded registered-key set must never be memoized. Callers pass
    //     caller-authored, unbounded names here (MCP tool names embed a per-run bundle id), so
    //     memoizing misses would reintroduce exactly the unbounded, process-lifetime memory growth
    //     this type's class remarks exist to prevent. A miss costs one HashSet probe; leave it live.
    //   * A CONSTRUCTION FAILURE must never be memoized either. It is the one genuinely transient
    //     outcome here, and caching it would permanently downgrade a security control (the caller
    //     falls back to key-only Deny/grant coverage) for the rest of the process on the strength of
    //     one bad moment. Retrying costs a DI probe that a healthy tool answers from its singleton.
    // Keyed case-insensitively to match this type's own resolution semantics (#655) — "BASH" and
    // "bash" resolve to one tool, so they share one memo entry.
    private readonly ConcurrentDictionary<string, string> _publishedNameByKey =
        new(StringComparer.OrdinalIgnoreCase);

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
    /// <para>
    /// #626 code-review: originally duplicated near-verbatim between <c>PluginPermissionRuleProvider</c>
    /// (#612) and <c>EnvelopePermissionRuleProvider</c> (#626) — exactly the anti-pattern this type's
    /// own class remarks say it exists to prevent (#387: "found duplicated — twice"). Deliberately pure
    /// (no logging): a construction failure is a caller-specific concern (each rule provider names a
    /// different kind of manifest entry in its own log message), so each caller still wraps this with
    /// its own one-line log-on-failure — only the resolve-or-fall-back-to-key logic itself is shared.
    /// </para>
    /// <para>
    /// <strong>A successful resolution is memoized for the process lifetime (#651), so a caller on a hot
    /// path does not need a cache of its own.</strong> The per-call cost lands on
    /// <c>CapabilityEnvelopeGrantResolver</c>, which is reached both from
    /// <c>EnvelopePermissionRuleProvider</c> (via <c>ThreePhasePermissionResolver.CollectRulesAsync</c>,
    /// which asks every rule provider for its rules on <em>every</em> tool-permission resolution) and
    /// from <c>ToolInvocationGovernor.EnvelopeGrantsToolWhenArmed</c>'s independent re-confirmation, and
    /// which re-expanded every granted/declared name on each of those calls. That is what #651 was
    /// filed against, and it is why the fix belongs here: the alternative was a second bespoke
    /// per-provider cache with its own separately-argued invalidation rule. The other caller,
    /// <c>PluginPermissionRuleProvider</c>, already avoids the repeat cost a different way — it caches
    /// its whole rule list against registry version counters (#612), so it reaches this method only on
    /// a recompute, and it benefits from the memo across those recomputes rather than per call. The
    /// mapping returned here cannot change once observed (see <see cref="_publishedNameByKey"/>), so
    /// memoizing it needs no invalidation at all. A miss (unknown name) and a construction failure are
    /// both deliberately NOT memoized — see that field's remarks for why each is a safety requirement
    /// rather than an oversight.
    /// </para>
    /// </remarks>
    public bool TryResolvePublishedName(string toolKey, out string publishedName, out Exception? constructionError)
    {
        // #651: a hit skips the DI probe AND the caller's own per-call caching concerns entirely —
        // see _publishedNameByKey's remarks for why this mapping can never go stale.
        //
        // The null check is load-bearing, not defensive noise: ConcurrentDictionary.TryGetValue THROWS
        // on a null key, whereas every pre-#651 path through this method reached the container inside
        // TryResolve's catch-all and so honoured the documented "returns false" contract instead. A
        // null key is reachable — PluginPermissionRuleProvider.EmitDeniedToolsRules forwards each
        // DeniedTools entry unfiltered, and that list is operator-authored config bound from JSON
        // (PluginDeclaration.DeniedTools), where ["bash", null] yields a null element that the
        // nullable-reference annotation cannot prevent at runtime. Since ThreePhasePermissionResolver
        // does not catch provider exceptions, letting it throw here would take down permission
        // resolution for every tool call in the host. Skipping the memo for a null key leaves such a
        // key on exactly its previous path, so this fixes the new crash without changing any existing
        // behaviour (it still returns false, with the same construction error attached).
        if (toolKey is not null && _publishedNameByKey.TryGetValue(toolKey, out var memoized))
        {
            publishedName = memoized;
            constructionError = null;
            return true;
        }

        // toolKey! asserts nothing new: the compiler only sees the null possibility because the guard
        // above had to test for it, and TryResolve's catch-all has always been what handles it.
        var tool = TryResolve(toolKey!, out constructionError);
        if (tool is not null)
        {
            publishedName = tool.Name;
            _publishedNameByKey[toolKey!] = publishedName;
            return true;
        }

        publishedName = toolKey!;
        return false;
    }
}
