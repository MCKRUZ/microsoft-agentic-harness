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

    // Separator between skill ids in a composite multi-skill cache key. A skill id is validated
    // kebab-case elsewhere in this harness, so U+0001 (a control character no legitimate skill id
    // can contain) cannot collide with a real id the way a printable separator like ',' could if a
    // skill id ever legitimately contained one.
    private const char CompositeKeySeparator = '';

    /// <inheritdoc />
    public IEgressPolicy ResolveFor(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var skillIds = _currentSkill.CurrentSkillIds;
        return skillIds.Count switch
        {
            0 => _noSkillPolicy.Value,
            1 => _skillCache.GetOrAdd(skillIds[0], BuildPolicyForSkill),
            _ => _skillCache.GetOrAdd(CompositeKey(skillIds), _ => BuildPolicyForSkills(skillIds)),
        };
    }

    /// <summary>
    /// A cache key for a multi-skill scope (#589) that cannot collide with any single skill id —
    /// every single-id key is a bare skill id with no <see cref="CompositeKeySeparator"/> in it, so a
    /// key containing that separator can never equal one.
    /// </summary>
    private static string CompositeKey(IReadOnlyList<string> skillIds) =>
        string.Join(CompositeKeySeparator, skillIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase));

    private IEgressPolicy BuildDefaultOnlyPolicy()
    {
        var defaultEntries = EgressAllowlistMapper.Map(_appConfig.CurrentValue.AI.Egress.DefaultAllowlist);
        return new DefaultEgressPolicy(defaultEntries, _policyLogger, _timeProvider);
    }

    private IEgressPolicy BuildPolicyForSkill(string key)
    {
        var defaultEntries = EgressAllowlistMapper.Map(_appConfig.CurrentValue.AI.Egress.DefaultAllowlist);
        var perSkill = SkillAllowlistEntries(key);
        if (perSkill.Count == 0)
        {
            // Skill has no additions — reuse the default-only policy shape.
            return new DefaultEgressPolicy(defaultEntries, _policyLogger, _timeProvider);
        }

        // Merge default + per-skill (ADDITIVE union). Duplicates are harmless;
        // the policy's match algorithm short-circuits on the first match.
        var merged = new List<EgressAllowlistEntry>(defaultEntries.Count + perSkill.Count);
        merged.AddRange(defaultEntries);
        merged.AddRange(perSkill);

        _logger.LogDebug(
            "Built egress policy for skill '{SkillId}': {DefaultCount} default + {PerSkillCount} per-skill entries.",
            key, defaultEntries.Count, perSkill.Count);

        return new DefaultEgressPolicy(merged, _policyLogger, _timeProvider);
    }

    /// <summary>
    /// The multi-skill counterpart of <see cref="BuildPolicyForSkill"/> (#589): default entries plus
    /// EVERY named skill's own allowlist additions, unioned. A tool name shared by two skills must
    /// resolve at least as broad an allowlist as either skill would grant it alone — this is the same
    /// additive contract <see cref="BuildPolicyForSkill"/> already applies to a single skill, extended
    /// to more than one active at once.
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
            "Built union egress policy for skills '{SkillIds}': {DefaultCount} default + {MergedCount} " +
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
