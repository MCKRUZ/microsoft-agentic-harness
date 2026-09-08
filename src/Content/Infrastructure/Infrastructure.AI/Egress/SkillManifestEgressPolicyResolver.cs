using System.Collections.Concurrent;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Egress;
using Application.AI.Common.Interfaces.Skills;
using Domain.AI.Egress;
using Domain.AI.Identity;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Egress;

/// <summary>
/// Per-skill <see cref="IEgressPolicyResolver"/> backed by the skill manifest.
/// Reads the current skill via <see cref="ICurrentSkillAccessor"/>, looks up
/// the skill's <c>egress.allowlist</c> via <see cref="ISkillMetadataRegistry"/>,
/// and returns an <see cref="IEgressPolicy"/> whose allowlist is the UNION of
/// the harness-wide <c>EgressConfig.DefaultAllowlist</c> and the per-skill
/// additions. Policies are cached by skill identifier so the merge runs at
/// most once per skill regardless of request volume.
/// </summary>
/// <remarks>
/// <para>
/// Per-skill allowlists are ADDITIVE — a skill may broaden outbound reach but
/// never narrows the default and never overrides another skill. A skill
/// without an <c>egress</c> manifest section (or with an empty allowlist)
/// resolves to the same policy as the no-skill default, so this resolver is
/// safe to install as the harness-wide replacement for
/// <see cref="DefaultEgressPolicyResolver"/>.
/// </para>
/// <para>
/// Cache safety: the cache key is the skill id; on cache miss the resolver
/// constructs a new <see cref="DefaultEgressPolicy"/> by merging the default
/// and per-skill entries, then stores it. The same identifier always returns
/// the same instance (test 6 in the PR-3c suite verifies this). The default
/// policy itself is computed once at construction and re-used for the
/// "no skill in scope" path.
/// </para>
/// <para>
/// Identity is intentionally not part of the cache key — egress allowlists are
/// keyed by SKILL, not by identity. Identity controls "which workload is
/// allowed to call out at all" (the identity check in the delegating handler);
/// the skill controls "which hosts that workload may reach". Mixing them would
/// fragment the cache without changing the verdict.
/// </para>
/// </remarks>
public sealed class SkillManifestEgressPolicyResolver : IEgressPolicyResolver
{
    private readonly ICurrentSkillAccessor _currentSkill;
    private readonly ISkillMetadataRegistry _skillRegistry;
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly ILogger<SkillManifestEgressPolicyResolver> _logger;
    private readonly ILogger<DefaultEgressPolicy> _policyLogger;
    private readonly TimeProvider _timeProvider;

    // Two separate caches rather than one keyed by skill id plus a reserved sentinel string (#531
    // security-review finding): the prior design's sentinel ("<no-skill>") relied on no real skill
    // ever being named that, case-insensitively, in a case-insensitive cache — a property nothing
    // enforced, and one the sentinel's own doc comment misstated (it claimed the safety came from
    // skill ids never containing spaces, which the sentinel itself doesn't contain and so proves
    // nothing about). A skill an operator names any case variant of the sentinel would collide in
    // the shared dictionary with the reserved "no skill" entry, in whichever direction lost the
    // race to populate the cache first — leaking that skill's widened allowlist onto every
    // unscoped call, or vice versa. Splitting the "no skill" case onto its own field makes the
    // collision structurally impossible rather than relying on a string never being reused.
    private readonly Lazy<IEgressPolicy> _noSkillPolicy;
    private readonly ConcurrentDictionary<string, IEgressPolicy> _skillCache = new(StringComparer.OrdinalIgnoreCase);

    // A separate cache, not a composite key sharing _skillCache's key space (#589 security-review
    // finding): a composite key sharing the single-skill cache's own key space would need an
    // unenforced assumption about what characters a skill id can contain to stay collision-free
    // against a bare id. A separate dictionary removes that collision class entirely — the same
    // pattern the no-skill/single-skill split above already uses for the identical reason.
    private readonly ConcurrentDictionary<string, IEgressPolicy> _multiSkillCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a new <see cref="SkillManifestEgressPolicyResolver"/>.</summary>
    public SkillManifestEgressPolicyResolver(
        ICurrentSkillAccessor currentSkill,
        ISkillMetadataRegistry skillRegistry,
        IOptionsMonitor<AppConfig> appConfig,
        ILogger<SkillManifestEgressPolicyResolver> logger,
        ILogger<DefaultEgressPolicy> policyLogger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(currentSkill);
        ArgumentNullException.ThrowIfNull(skillRegistry);
        ArgumentNullException.ThrowIfNull(appConfig);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(policyLogger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _currentSkill = currentSkill;
        _skillRegistry = skillRegistry;
        _appConfig = appConfig;
        _logger = logger;
        _policyLogger = policyLogger;
        _timeProvider = timeProvider;
        _noSkillPolicy = new Lazy<IEgressPolicy>(BuildDefaultOnlyPolicy);
    }

    /// <inheritdoc />
    public IEgressPolicy ResolveFor(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var skillIds = _currentSkill.CurrentSkillIds;
        return skillIds.Count switch
        {
            0 => _noSkillPolicy.Value,
            1 => _skillCache.GetOrAdd(skillIds[0], key => BuildPolicyForSkills([key])),
            _ => _multiSkillCache.GetOrAdd(CompositeKey(skillIds), _ => BuildPolicyForSkills(skillIds)),
        };
    }

    /// <summary>
    /// A deterministic, collision-free cache key for a multi-skill scope (#589), for
    /// <see cref="_multiSkillCache"/>'s own key space only.
    /// </summary>
    /// <remarks>
    /// Length-prefixes each sorted id as <c>"{length}:{id}"</c> — not a bare separator join
    /// (code-review finding: no validator in this codebase restricts what characters a skill id can
    /// contain, so a bare-separator join could ambiguously collide, e.g. ids <c>["a", "bc"]</c> and
    /// <c>["ab", "c"]</c> sort and join to the identical string with no separator, or the same
    /// collision recurs one level up if the separator itself can appear inside an id) — and not a bare
    /// length prefix with no delimiter either (a SECOND round of review caught this same fix's own
    /// first version: <c>"{length}{id}"</c> is not actually unambiguous, because the decimal length
    /// digits are not delimited from the content that follows — a digit-leading id can extend what
    /// looks like the length of the PRECEDING segment, e.g. ids <c>["2", "abcdefghij"]</c> (lengths 1,
    /// 10) and a single id <c>["10abcdefghij"]</c> (length 12) both produce <c>"1210abcdefghij"</c>).
    /// The <c>:</c> delimiter is what actually closes the gap: it can never be a decimal digit, so the
    /// length-digit run for each segment always terminates at the first <c>:</c> regardless of what the
    /// id itself contains, and exactly that many characters are then consumed as the segment's content
    /// before the next length-digit run begins.
    /// </remarks>
    private static string CompositeKey(IReadOnlyList<string> skillIds) =>
        string.Concat(skillIds
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Select(id => $"{id.Length}:{id}"));

    private IEgressPolicy BuildDefaultOnlyPolicy()
    {
        var defaultEntries = EgressAllowlistMapper.Map(_appConfig.CurrentValue.AI.Egress.DefaultAllowlist);
        return new DefaultEgressPolicy(defaultEntries, _policyLogger, _timeProvider);
    }

    /// <summary>
    /// Default entries plus every named skill's own allowlist additions, unioned — the single
    /// implementation for both the single- and multi-skill resolve paths (#589 code-review finding:
    /// these were two near-identical hand-written copies of the same merge; the one-skill case is
    /// just this with a one-element list, so there is no reason for a second implementation to drift
    /// from). A tool name shared by two skills must resolve at least as broad an allowlist as either
    /// skill would grant it alone.
    /// </summary>
    private IEgressPolicy BuildPolicyForSkills(IReadOnlyList<string> skillIds)
    {
        var defaultEntries = EgressAllowlistMapper.Map(_appConfig.CurrentValue.AI.Egress.DefaultAllowlist);

        var merged = new List<EgressAllowlistEntry>(defaultEntries);
        foreach (var skillId in skillIds)
            merged.AddRange(SkillAllowlistEntries(skillId));

        if (merged.Count == defaultEntries.Count)
            return new DefaultEgressPolicy(defaultEntries, _policyLogger, _timeProvider);

        _logger.LogDebug(
            "Built egress policy for skill(s) '{SkillIds}': {DefaultCount} default + {AddedCount} " +
            "combined per-skill entries.",
            string.Join(", ", skillIds), defaultEntries.Count, merged.Count - defaultEntries.Count);

        return new DefaultEgressPolicy(merged, _policyLogger, _timeProvider);
    }

    /// <summary>
    /// One skill's own manifest allowlist entries — empty when the skill is unknown (logged) or
    /// declares none. Shared by the single- and multi-skill resolve paths so the lookup and the
    /// unknown-skill warning are written once.
    /// </summary>
    private IReadOnlyList<EgressAllowlistEntry> SkillAllowlistEntries(string skillId)
    {
        var skill = _skillRegistry.TryGet(skillId);
        if (skill is null)
        {
            _logger.LogWarning(
                "Egress policy lookup for unknown skill '{SkillId}' — no per-skill entries contributed.",
                skillId);
            return [];
        }

        return skill.Egress?.Allowlist ?? [];
    }
}
