using Application.Core.Permissions;
using Domain.AI.Changes;
using Domain.AI.Governance;
using Domain.Common.Config;
using Domain.Common.Config.AI.Permissions;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.Core.Tests.Permissions;

/// <summary>
/// Tests for <see cref="AutonomyDecisionEvaluator"/> — the Application-side
/// layered evaluator. Covers fallback path, environment overlay, per-skill
/// narrowing, and the dual-key state-changer opt-in.
/// </summary>
public sealed class AutonomyDecisionEvaluatorTests
{
    private static AutonomyDecisionEvaluator Build(AppConfig config, string environmentName = "Development")
    {
        var monitor = Mock.Of<IOptionsMonitor<AppConfig>>(o => o.CurrentValue == config);

        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(environmentName);

        return new AutonomyDecisionEvaluator(
            monitor,
            env.Object,
            NullLogger<AutonomyDecisionEvaluator>.Instance);
    }

    private static AppConfig GradedDisabled() => new();

    private static AppConfig GradedEnabled(Action<GradedAutonomyConfig>? configure = null)
    {
        var config = new AppConfig();
        config.AI.Permissions.GradedAutonomy.Enabled = true;
        configure?.Invoke(config.AI.Permissions.GradedAutonomy);
        return config;
    }

    // ─────────────────────────────────────────────────────────────────────
    // 1. Fallback path — Graded disabled preserves PR-2 behaviour exactly.
    // ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(BlastRadius.Trivial, AutonomyDecision.AutoApprove)]
    [InlineData(BlastRadius.Low, AutonomyDecision.RequiresApproval)]
    [InlineData(BlastRadius.Medium, AutonomyDecision.RequiresApproval)]
    [InlineData(BlastRadius.High, AutonomyDecision.RequiresApproval)]
    [InlineData(BlastRadius.Critical, AutonomyDecision.RequiresApproval)]
    public void Evaluate_GradedDisabled_FallsBackToPR2Behavior(
        BlastRadius radius, AutonomyDecision expected)
    {
        var sut = Build(GradedDisabled());

        var result = sut.Evaluate(
            AutonomyLevel.Autonomous,
            radius,
            ChangeTargetKind.GitRepo,
            isStateChange: false,
            skillKey: "any-skill");

        result.Decision.Should().Be(expected);
        result.Reason.Should().Contain("GradedAutonomy disabled");
    }

    // ─────────────────────────────────────────────────────────────────────
    // 2. Per-environment override.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_DevelopmentPermissiveRule_AllowsAutoApprove()
    {
        var config = GradedEnabled(g =>
        {
            g.PerEnvironment["Development"] = new EnvironmentAutonomyConfig
            {
                PerBlastRadius =
                {
                    ["Medium"] = new BlastRadiusRuleConfig { Decision = "AutoApprove" }
                }
            };
        });

        var sut = Build(config, environmentName: "Development");

        var result = sut.Evaluate(
            AutonomyLevel.Autonomous,
            BlastRadius.Medium,
            ChangeTargetKind.GitRepo,
            isStateChange: false,
            skillKey: null);

        result.Decision.Should().Be(AutonomyDecision.AutoApprove);
        result.Environment.Should().Be("Development");
    }

    [Fact]
    public void Evaluate_ProductionStrictRule_SameTier_StillRequiresApproval()
    {
        // Same tier (Autonomous), same radius (Medium) — but Production env has
        // no rule, falls back to policy default (RequiresApproval).
        var config = GradedEnabled(g =>
        {
            g.PerEnvironment["Development"] = new EnvironmentAutonomyConfig
            {
                PerBlastRadius =
                {
                    ["Medium"] = new BlastRadiusRuleConfig { Decision = "AutoApprove" }
                }
            };
        });

        var sut = Build(config, environmentName: "Production");

        var result = sut.Evaluate(
            AutonomyLevel.Autonomous,
            BlastRadius.Medium,
            ChangeTargetKind.GitRepo,
            isStateChange: false,
            skillKey: null);

        result.Decision.Should().Be(AutonomyDecision.RequiresApproval);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 3. Per-skill override is more restrictive only.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_PerSkillTierStricterThanBaseline_NarrowsTier()
    {
        var config = GradedEnabled(g =>
        {
            g.PerSkill["careful-skill"] = new SkillAutonomyConfig
            {
                Tier = "Restricted"
            };

            // Permissive env rule that the careful-skill tier narrowing should ignore
            g.PerEnvironment["Development"] = new EnvironmentAutonomyConfig
            {
                PerBlastRadius =
                {
                    ["Low"] = new BlastRadiusRuleConfig { Decision = "AutoApprove" }
                }
            };
        });

        var sut = Build(config, environmentName: "Development");

        // Skill narrows tier to Restricted; Restricted has no auto-approve rules
        // → still RequiresApproval despite the env Auto-approve rule, because the
        // policy is built off the narrowed tier whose rule table is the env one
        // — but the SAFETY default still kicks in for state changes etc.
        var result = sut.Evaluate(
            AutonomyLevel.Autonomous,
            BlastRadius.Low,
            ChangeTargetKind.GitRepo,
            isStateChange: false,
            skillKey: "careful-skill");

        result.Tier.Should().Be(AutonomyLevel.Restricted);
    }

    [Fact]
    public void Evaluate_PerSkillTierLooserThanBaseline_IgnoredAndKeepsBaseline()
    {
        var config = GradedEnabled(g =>
        {
            g.PerSkill["bossy-skill"] = new SkillAutonomyConfig
            {
                Tier = "Autonomous"
            };
        });

        var sut = Build(config);

        var result = sut.Evaluate(
            AutonomyLevel.Restricted,  // baseline is strict
            BlastRadius.Low,
            ChangeTargetKind.GitRepo,
            isStateChange: false,
            skillKey: "bossy-skill");

        // Skill tried to widen Restricted → Autonomous, must be ignored.
        result.Tier.Should().Be(AutonomyLevel.Restricted);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 4. Per-environment vs Production — different decisions for same radius.
    // ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Development", AutonomyDecision.AutoApprove)]
    [InlineData("Production", AutonomyDecision.RequiresApproval)]
    public void Evaluate_SamePolicy_DifferentEnvironments_DifferentDecisions(
        string environmentName, AutonomyDecision expected)
    {
        var config = GradedEnabled(g =>
        {
            g.PerEnvironment["Development"] = new EnvironmentAutonomyConfig
            {
                PerBlastRadius =
                {
                    ["Medium"] = new BlastRadiusRuleConfig { Decision = "AutoApprove" }
                }
            };
            // Production gets no rule for Medium — falls back to RequiresApproval.
        });

        var sut = Build(config, environmentName);

        var result = sut.Evaluate(
            AutonomyLevel.Autonomous,
            BlastRadius.Medium,
            ChangeTargetKind.GitRepo,
            isStateChange: false,
            skillKey: null);

        result.Decision.Should().Be(expected);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 5. Dual-key state-change check.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_StateChange_RowOptInButSkillNotInOptIns_StillRequiresApproval()
    {
        var config = GradedEnabled(g =>
        {
            g.PerEnvironment["Development"] = new EnvironmentAutonomyConfig
            {
                PerBlastRadius =
                {
                    ["Low"] = new BlastRadiusRuleConfig
                    {
                        Decision = "AutoApprove",
                        AllowAutoApproveForStateChange = true
                    }
                }
            };
            // Note: NO entry in g.StateChangerOptIns for "writer-skill"
        });

        var sut = Build(config);

        var result = sut.Evaluate(
            AutonomyLevel.Autonomous,
            BlastRadius.Low,
            ChangeTargetKind.GitRepo,
            isStateChange: true,
            skillKey: "writer-skill");

        result.Decision.Should().Be(AutonomyDecision.RequiresApproval);
        result.Reason.Should().Contain("dual-key");
    }

    [Fact]
    public void Evaluate_StateChange_BothOptInsSet_AutoApproves()
    {
        var config = GradedEnabled(g =>
        {
            g.PerEnvironment["Development"] = new EnvironmentAutonomyConfig
            {
                PerBlastRadius =
                {
                    ["Low"] = new BlastRadiusRuleConfig
                    {
                        Decision = "AutoApprove",
                        AllowAutoApproveForStateChange = true
                    }
                }
            };
            g.StateChangerOptIns.Add("writer-skill");
        });

        var sut = Build(config);

        var result = sut.Evaluate(
            AutonomyLevel.Autonomous,
            BlastRadius.Low,
            ChangeTargetKind.GitRepo,
            isStateChange: true,
            skillKey: "writer-skill");

        result.Decision.Should().Be(AutonomyDecision.AutoApprove);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 6. Critical always requires approval, even with permissive config.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_CriticalRadius_PermissiveProdConfig_StillRequiresApproval()
    {
        var config = GradedEnabled(g =>
        {
            g.PerEnvironment["Production"] = new EnvironmentAutonomyConfig
            {
                PerBlastRadius =
                {
                    ["Critical"] = new BlastRadiusRuleConfig
                    {
                        Decision = "AutoApprove",
                        AllowAutoApproveForStateChange = true
                    }
                }
            };
            g.StateChangerOptIns.Add("any-skill");
        });

        var sut = Build(config, "Production");

        var result = sut.Evaluate(
            AutonomyLevel.Autonomous,
            BlastRadius.Critical,
            ChangeTargetKind.IacDeployment,
            isStateChange: true,
            skillKey: "any-skill");

        result.Decision.Should().Be(AutonomyDecision.RequiresApproval);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 6. Name-only parsing of config rows (#300).
    // ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("2")]                       // the numeric form of Medium
    [InlineData(" 2")]                      // and behind a stray space
    [InlineData("99")]                      // outside the defined range
    [InlineData("Low,Medium")]              // comma-composite, OR'd to Medium
    public void Evaluate_NonNameBlastRadiusRowKey_DropsTheRowRatherThanApplyingIt(string rowKey)
    {
        // #300. A permissive parse lets a numeric row key address a radius positionally, so "2"
        // silently becomes the Medium AutoApprove rule the operator never wrote by name. Refusing it
        // drops the row and the policy default (RequiresApproval) stands — the safe direction, and
        // the one AutonomyConfigValidator already refuses to boot with.
        var config = GradedEnabled(g =>
        {
            g.PerEnvironment["Development"] = new EnvironmentAutonomyConfig
            {
                PerBlastRadius =
                {
                    [rowKey] = new BlastRadiusRuleConfig { Decision = "AutoApprove" }
                }
            };
        });

        var sut = Build(config, environmentName: "Development");

        var result = sut.Evaluate(
            AutonomyLevel.Autonomous,
            BlastRadius.Medium,
            ChangeTargetKind.GitRepo,
            isStateChange: false,
            skillKey: null);

        result.Decision.Should().Be(AutonomyDecision.RequiresApproval);
    }

    [Theory]
    [InlineData("0")]                       // the numeric form of AutoApprove
    [InlineData(" 0")]
    [InlineData("RequiresApproval,AutoApprove")]
    public void Evaluate_NonNameDecision_DefaultsToRequiresApproval(string decision)
    {
        // The row key is valid here, so the row is kept — but its Decision is not a name. The
        // documented fallback is RequiresApproval, and a permissive parse would instead read "0" as
        // AutoApprove and hand back the loosest possible answer off a typo.
        var config = GradedEnabled(g =>
        {
            g.PerEnvironment["Development"] = new EnvironmentAutonomyConfig
            {
                PerBlastRadius =
                {
                    ["Medium"] = new BlastRadiusRuleConfig { Decision = decision }
                }
            };
        });

        var sut = Build(config, environmentName: "Development");

        var result = sut.Evaluate(
            AutonomyLevel.Autonomous,
            BlastRadius.Medium,
            ChangeTargetKind.GitRepo,
            isStateChange: false,
            skillKey: null);

        result.Decision.Should().Be(AutonomyDecision.RequiresApproval);
    }

    [Fact]
    public void Evaluate_NamedRowsAreStillApplied()
    {
        // The control for both theories above: refusing non-names must not mean ignoring config.
        // This is the same rule as the first theory, written by name, and it does auto-approve.
        var config = GradedEnabled(g =>
        {
            g.PerEnvironment["Development"] = new EnvironmentAutonomyConfig
            {
                PerBlastRadius =
                {
                    [nameof(BlastRadius.Medium)] = new BlastRadiusRuleConfig
                    {
                        Decision = nameof(AutonomyDecision.AutoApprove)
                    }
                }
            };
        });

        var sut = Build(config, environmentName: "Development");

        var result = sut.Evaluate(
            AutonomyLevel.Autonomous,
            BlastRadius.Medium,
            ChangeTargetKind.GitRepo,
            isStateChange: false,
            skillKey: null);

        result.Decision.Should().Be(AutonomyDecision.AutoApprove);
    }

    [Theory]
    [InlineData("2")]                       // the numeric form of Autonomous
    [InlineData("99")]
    [InlineData("Restricted,Autonomous")]
    public void Evaluate_NonNamePerSkillTier_KeepsTheBaselineTier(string tier)
    {
        // A per-skill tier may only narrow the baseline. A permissive parse would accept "2" as
        // Autonomous — and an out-of-range number sorts above every real tier, so the `skillTier >=
        // baseline` guard that blocks loosening would still reject it, but only by luck of the
        // ordering. Refusing the value keeps the baseline for the stated reason instead.
        var config = GradedEnabled(g =>
        {
            g.PerSkill["some-skill"] = new SkillAutonomyConfig { Tier = tier };
        });

        var sut = Build(config, environmentName: "Development");

        var result = sut.Evaluate(
            AutonomyLevel.Supervised,
            BlastRadius.Low,
            ChangeTargetKind.GitRepo,
            isStateChange: false,
            skillKey: "some-skill");

        result.Tier.Should().Be(AutonomyLevel.Supervised);
    }
}
