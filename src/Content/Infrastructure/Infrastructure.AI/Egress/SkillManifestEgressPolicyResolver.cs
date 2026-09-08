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

    // No-skill policy stays on its own field rather than a reserved sentinel key sharing the skill
    // cache's key space (#531 security-review finding): the prior design's sentinel ("<no-skill>")
    // relied on no real skill ever being named that, case-insensitively, in a case-insensitive
    // cache — a property nothing enforced, and one the sentinel's own doc comment misstated (it
    // claimed the safety came from skill ids never containing spaces, which the sentinel itself
    // doesn't contain and so proves nothing about). Splitting the "no skill" case onto its own
    // field makes the collision structurally impossible rather than relying on a string never
    // being reused.
    private readonly Lazy<IEgressPolicy> _noSkillPolicy;

    // One cache for every non-empty skill scope, single- or multi-skill alike, keyed by structural
    // (order-independent, case-insensitive) list equality rather than a string encoding of the list
    // (#589 simplification: an earlier version kept a separate string-keyed cache per arity, with a
    // hand-proved collision-free length-prefix encoding for the multi-skill key — see git history —
    // that took two review rounds to actually get right. A comparer keyed directly on the list
    // sidesteps that whole class of encoding-collision reasoning: two lists are the same cache slot
    // exactly when SkillIdListComparer says they're equal, by construction, for any arity).
    private readonly ConcurrentDictionary<IReadOnlyList<string>, IEgressPolicy> _skillCache =
        new(SkillIdListComparer.Instance);

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
        return skillIds.Count == 0
            ? _noSkillPolicy.Value
            : _skillCache.GetOrAdd(skillIds, static (ids, self) => self.BuildPolicyForSkills(ids), this);
    }

    /// <summary>
    /// Order-independent, case-insensitive structural equality over a skill-id list — what makes
    /// <see cref="_skillCache"/> safe to key directly on the list rather than on an encoded string.
    /// </summary>
    private sealed class SkillIdListComparer : IEqualityComparer<IReadOnlyList<string>>
    {
        public static readonly SkillIdListComparer Instance = new();

        public bool Equals(IReadOnlyList<string>? x, IReadOnlyList<string>? y)
        {
            if (ReferenceEquals(x, y))
                return true;
            if (x is null || y is null || x.Count != y.Count)
                return false;

            return x.OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(y.OrderBy(id => id, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        }

        public int GetHashCode(IReadOnlyList<string> obj)
        {
            var hash = new HashCode();
            foreach (var id in obj.OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
                hash.Add(id, StringComparer.OrdinalIgnoreCase);
            return hash.ToHashCode();
        }
    }

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
