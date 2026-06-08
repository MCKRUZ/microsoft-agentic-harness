using Application.AI.Common.Interfaces.Governance;
using Domain.AI.Changes;
using Domain.AI.Governance;
using Domain.AI.Permissions;
using Domain.Common.Config;
using Domain.Common.Config.AI.Permissions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.Core.Permissions;

/// <summary>
/// Default <see cref="IAutonomyDecisionEvaluator"/>: layers the configured
/// <see cref="GradedAutonomyConfig"/> overrides on top of the tier defaults and
/// returns the resulting <see cref="AutonomyDecisionResult"/>. Layering order:
/// </summary>
/// <list type="number">
///   <item><description>Resolve effective tier (skill cannot loosen).</description></item>
///   <item><description>Build the tier's <see cref="AutonomyTierPolicy"/> from per-environment, then per-skill rules.</description></item>
///   <item><description>Call <see cref="AutonomyTierPolicy.EvaluateFor"/>.</description></item>
///   <item><description>Apply the state-changer opt-in dual-key check at the end.</description></item>
/// </list>
/// <remarks>
/// <para>
/// When <see cref="GradedAutonomyConfig.Enabled"/> is false the evaluator returns
/// the PR-2 fallback: <see cref="AutonomyDecision.AutoApprove"/> for
/// <see cref="BlastRadius.Trivial"/>, <see cref="AutonomyDecision.RequiresApproval"/>
/// for anything else. This preserves PR-2's existing tests without modification.
/// </para>
/// <para>
/// Misconfig handling: invalid enum strings in config produce log warnings and the
/// row is treated as <see cref="AutonomyDecision.RequiresApproval"/> — safer than
/// throwing at evaluation time and stranding a proposal. Boot-time validation in
/// <c>AutonomyConfigValidator</c> catches misconfigurations earlier and louder.
/// </para>
/// </remarks>
public sealed class AutonomyDecisionEvaluator : IAutonomyDecisionEvaluator
{
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly IHostEnvironment _hostEnvironment;
    private readonly ILogger<AutonomyDecisionEvaluator> _logger;

    /// <summary>Initializes a new <see cref="AutonomyDecisionEvaluator"/>.</summary>
    /// <param name="options">Application config, read fresh on each call so hot-reload propagates immediately.</param>
    /// <param name="hostEnvironment">Host environment used to pick the per-environment overlay.</param>
    /// <param name="logger">Logger for misconfig warnings.</param>
    public AutonomyDecisionEvaluator(
        IOptionsMonitor<AppConfig> options,
        IHostEnvironment hostEnvironment,
        ILogger<AutonomyDecisionEvaluator> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(hostEnvironment);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _hostEnvironment = hostEnvironment;
        _logger = logger;
    }

    /// <inheritdoc />
    public AutonomyDecisionResult Evaluate(
        AutonomyLevel tier,
        BlastRadius radius,
        ChangeTargetKind targetKind,
        bool isStateChange,
        string? skillKey)
    {
        var permissions = _options.CurrentValue.AI.Permissions;
        var graded = permissions.GradedAutonomy;
        var environmentName = _hostEnvironment.EnvironmentName ?? "Unknown";

        // Fallback path — PR-2 behaviour preserved verbatim.
        if (!graded.Enabled)
        {
            var fallbackDecision = radius == BlastRadius.Trivial
                ? AutonomyDecision.AutoApprove
                : AutonomyDecision.RequiresApproval;

            // Even in fallback mode the state-changer invariant holds for non-Trivial.
            // For Trivial state-changes the PR-2 resolver already auto-approves, so we
            // preserve that exact behaviour for parity with the existing tests.
            return new AutonomyDecisionResult(
                fallbackDecision,
                tier,
                radius,
                targetKind,
                isStateChange,
                environmentName,
                skillKey,
                "GradedAutonomy disabled — PR-2 fallback applied (Trivial → AutoApprove, else → RequiresApproval).");
        }

        // 1. Effective tier (skill cannot loosen the resolved tier).
        var (effectiveTier, tierReason) = ResolveEffectiveTier(tier, skillKey, graded);

        // 2. Build the policy: environment row → skill row → defaults.
        var policy = BuildPolicy(effectiveTier, environmentName, skillKey, graded);

        // 3. Apply policy.
        var (decision, policyReason) = policy.EvaluateFor(radius, targetKind, isStateChange);

        // 4. Dual-key state-change opt-in. If the policy says AutoApprove for a
        //    state-change, the skill must ALSO be in StateChangerOptIns. The policy
        //    has already checked the per-row flag; this is the second guard.
        if (decision == AutonomyDecision.AutoApprove
            && isStateChange
            && (skillKey is null || !graded.StateChangerOptIns.Contains(skillKey)))
        {
            return new AutonomyDecisionResult(
                AutonomyDecision.RequiresApproval,
                effectiveTier,
                radius,
                targetKind,
                isStateChange,
                environmentName,
                skillKey,
                $"{policyReason} BUT state-change skill '{skillKey ?? "<none>"}' is not in StateChangerOptIns — " +
                "the dual-key safety check forces RequiresApproval.");
        }

        return new AutonomyDecisionResult(
            decision,
            effectiveTier,
            radius,
            targetKind,
            isStateChange,
            environmentName,
            skillKey,
            $"{tierReason} {policyReason}");
    }

    private (AutonomyLevel Tier, string Reason) ResolveEffectiveTier(
        AutonomyLevel baseline,
        string? skillKey,
        GradedAutonomyConfig graded)
    {
        if (skillKey is null
            || !graded.PerSkill.TryGetValue(skillKey, out var skillConfig)
            || skillConfig.Tier is null)
        {
            return (baseline, $"using baseline tier {baseline}.");
        }

        if (!Enum.TryParse<AutonomyLevel>(skillConfig.Tier, ignoreCase: true, out var skillTier))
        {
            _logger.LogWarning(
                "Invalid PerSkill.Tier '{Tier}' for skill '{Skill}' — ignoring per-skill tier override.",
                skillConfig.Tier, skillKey);
            return (baseline, $"using baseline tier {baseline} (per-skill tier '{skillConfig.Tier}' was invalid).");
        }

        // Skill may only narrow — pick the lower numeric value (Restricted < Supervised < Autonomous).
        if (skillTier >= baseline)
        {
            _logger.LogWarning(
                "Per-skill tier '{SkillTier}' for skill '{Skill}' is not stricter than baseline '{Baseline}' — " +
                "ignoring (skill cannot loosen).",
                skillTier, skillKey, baseline);
            return (baseline, $"using baseline tier {baseline} (per-skill '{skillTier}' would loosen — rejected).");
        }

        return (skillTier, $"per-skill tier override {skillTier} (narrowed from baseline {baseline}).");
    }

    private AutonomyTierPolicy BuildPolicy(
        AutonomyLevel tier,
        string environmentName,
        string? skillKey,
        GradedAutonomyConfig graded)
    {
        // Start with environment overlay; layer per-skill on top.
        var radiusMap = new Dictionary<BlastRadius, BlastRadiusAutonomyRule>();

        if (graded.PerEnvironment.TryGetValue(environmentName, out var envConfig))
        {
            foreach (var (radiusName, ruleConfig) in envConfig.PerBlastRadius)
            {
                if (TryParseRule(radiusName, ruleConfig, $"PerEnvironment[{environmentName}]", out var rule))
                {
                    radiusMap[rule.Radius] = rule;
                }
            }
        }

        if (skillKey is not null && graded.PerSkill.TryGetValue(skillKey, out var skillConfig))
        {
            foreach (var (radiusName, ruleConfig) in skillConfig.PerBlastRadius)
            {
                if (TryParseRule(radiusName, ruleConfig, $"PerSkill[{skillKey}]", out var rule))
                {
                    // Per-skill may only narrow: prefer existing env row if it is stricter (or equal).
                    if (radiusMap.TryGetValue(rule.Radius, out var existing)
                        && IsStricter(existing.Decision, rule.Decision))
                    {
                        _logger.LogWarning(
                            "Per-skill rule for skill '{Skill}' radius '{Radius}' would loosen the resolved decision " +
                            "from {Existing} to {Attempt} — ignoring.",
                            skillKey, rule.Radius, existing.Decision, rule.Decision);
                        continue;
                    }

                    radiusMap[rule.Radius] = rule;
                }
            }
        }

        return new AutonomyTierPolicy
        {
            Level = tier,
            // DefaultBehavior is irrelevant for graded autonomy — it drives PermissionRule emission only.
            // We pick a reasonable per-tier default so the record is constructible.
            DefaultBehavior = tier == AutonomyLevel.Autonomous
                ? PermissionBehaviorType.Allow
                : PermissionBehaviorType.Ask,
            PerBlastRadius = radiusMap.Count == 0 ? null : radiusMap
        };
    }

    private bool TryParseRule(
        string radiusName,
        BlastRadiusRuleConfig ruleConfig,
        string source,
        out BlastRadiusAutonomyRule rule)
    {
        rule = default!;

        if (!Enum.TryParse<BlastRadius>(radiusName, ignoreCase: true, out var radius))
        {
            _logger.LogWarning(
                "Invalid BlastRadius name '{Radius}' in {Source} — skipping row.",
                radiusName, source);
            return false;
        }

        if (!Enum.TryParse<AutonomyDecision>(ruleConfig.Decision, ignoreCase: true, out var decision))
        {
            _logger.LogWarning(
                "Invalid AutonomyDecision '{Decision}' in {Source}[{Radius}] — defaulting to RequiresApproval.",
                ruleConfig.Decision, source, radiusName);
            decision = AutonomyDecision.RequiresApproval;
        }

        rule = new BlastRadiusAutonomyRule(
            radius,
            decision,
            ruleConfig.AllowAutoApproveForStateChange);
        return true;
    }

    /// <summary>
    /// True when <paramref name="a"/> is strictly more restrictive than <paramref name="b"/>.
    /// Forbidden &gt; RequiresApproval &gt; AutoApprove.
    /// </summary>
    private static bool IsStricter(AutonomyDecision a, AutonomyDecision b)
        => RestrictivenessRank(a) > RestrictivenessRank(b);

    private static int RestrictivenessRank(AutonomyDecision d) => d switch
    {
        AutonomyDecision.AutoApprove => 0,
        AutonomyDecision.RequiresApproval => 1,
        AutonomyDecision.Forbidden => 2,
        _ => 0
    };
}
