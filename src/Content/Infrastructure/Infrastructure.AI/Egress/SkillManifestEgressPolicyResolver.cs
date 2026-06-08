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
    /// <summary>
    /// Cache key reserved for the "no skill active" path. Distinct from any
    /// valid skill id (skill ids cannot contain spaces in the discovery flow).
    /// </summary>
    private const string NoSkillKey = "<no-skill>";

    private readonly ICurrentSkillAccessor _currentSkill;
    private readonly ISkillMetadataRegistry _skillRegistry;
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly ILogger<SkillManifestEgressPolicyResolver> _logger;
    private readonly ILogger<DefaultEgressPolicy> _policyLogger;
    private readonly TimeProvider _timeProvider;

    private readonly ConcurrentDictionary<string, IEgressPolicy> _cache = new(StringComparer.OrdinalIgnoreCase);

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
    }

    /// <inheritdoc />
    public IEgressPolicy ResolveFor(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var key = _currentSkill.CurrentSkillId ?? NoSkillKey;
        return _cache.GetOrAdd(key, BuildPolicy);
    }

    private IEgressPolicy BuildPolicy(string key)
    {
        var defaultEntries = EgressAllowlistMapper.Map(_appConfig.CurrentValue.AI.Egress.DefaultAllowlist);

        if (string.Equals(key, NoSkillKey, StringComparison.Ordinal))
        {
            return new DefaultEgressPolicy(defaultEntries, _policyLogger, _timeProvider);
        }

        var skill = _skillRegistry.TryGet(key);
        if (skill is null)
        {
            _logger.LogWarning(
                "Egress policy lookup for unknown skill '{SkillId}' — falling back to harness-wide default.",
                key);
            return new DefaultEgressPolicy(defaultEntries, _policyLogger, _timeProvider);
        }

        var perSkill = skill.Egress?.Allowlist ?? [];
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
}
