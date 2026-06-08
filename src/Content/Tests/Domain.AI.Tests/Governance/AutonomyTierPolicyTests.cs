using Domain.AI.Changes;
using Domain.AI.Governance;
using Domain.AI.Permissions;
using Xunit;

namespace Domain.AI.Tests.Governance;

/// <summary>
/// Tests for <see cref="AutonomyTierPolicy.EvaluateFor"/> — the pure Domain
/// projection used by the graded-autonomy evaluator. The matrix tests cover
/// the full tier × blast-radius cross-product and lock in the load-bearing
/// safety invariants (Critical always requires approval, state-changers
/// default to RequiresApproval).
/// </summary>
public sealed class AutonomyTierPolicyTests
{
    private static AutonomyTierPolicy PolicyWith(
        AutonomyLevel level,
        params BlastRadiusAutonomyRule[] rules)
        => new()
        {
            Level = level,
            DefaultBehavior = level == AutonomyLevel.Autonomous
                ? PermissionBehaviorType.Allow
                : PermissionBehaviorType.Ask,
            PerBlastRadius = rules.Length == 0
                ? null
                : rules.ToDictionary(r => r.Radius)
        };

    // ─────────────────────────────────────────────────────────────────────
    // 1. Tier × blast-radius matrix — Restricted always RequiresApproval.
    // ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(BlastRadius.Trivial)]
    [InlineData(BlastRadius.Low)]
    [InlineData(BlastRadius.Medium)]
    [InlineData(BlastRadius.High)]
    [InlineData(BlastRadius.Critical)]
    public void EvaluateFor_RestrictedTier_AnyRadius_AlwaysRequiresApproval(BlastRadius radius)
    {
        // Restricted tier has no auto-approve rules — every radius defaults
        // to RequiresApproval per the safety-default rule on the policy.
        var policy = PolicyWith(AutonomyLevel.Restricted);

        var (decision, _) = policy.EvaluateFor(radius, ChangeTargetKind.GitRepo, isStateChange: false);

        Assert.Equal(AutonomyDecision.RequiresApproval, decision);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 2. Tier × blast-radius matrix — Supervised default behaviour.
    // ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(BlastRadius.Trivial)]
    [InlineData(BlastRadius.Low)]
    [InlineData(BlastRadius.Medium)]
    [InlineData(BlastRadius.High)]
    [InlineData(BlastRadius.Critical)]
    public void EvaluateFor_SupervisedTier_NoRules_AnyRadius_RequiresApproval(BlastRadius radius)
    {
        var policy = PolicyWith(AutonomyLevel.Supervised);

        var (decision, _) = policy.EvaluateFor(radius, ChangeTargetKind.GitRepo, isStateChange: false);

        Assert.Equal(AutonomyDecision.RequiresApproval, decision);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 3. Tier × blast-radius matrix — Autonomous with permissive rules.
    // ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(BlastRadius.Trivial, AutonomyDecision.AutoApprove)]
    [InlineData(BlastRadius.Low, AutonomyDecision.AutoApprove)]
    [InlineData(BlastRadius.Medium, AutonomyDecision.RequiresApproval)]
    [InlineData(BlastRadius.High, AutonomyDecision.RequiresApproval)]
    [InlineData(BlastRadius.Critical, AutonomyDecision.RequiresApproval)] // the invariant
    public void EvaluateFor_AutonomousTier_PermissiveLowAndTrivial_RespectsCriticalGuard(
        BlastRadius radius, AutonomyDecision expected)
    {
        var policy = PolicyWith(
            AutonomyLevel.Autonomous,
            new BlastRadiusAutonomyRule(BlastRadius.Trivial, AutonomyDecision.AutoApprove),
            new BlastRadiusAutonomyRule(BlastRadius.Low, AutonomyDecision.AutoApprove));

        var (decision, _) = policy.EvaluateFor(radius, ChangeTargetKind.GitRepo, isStateChange: false);

        Assert.Equal(expected, decision);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 4. Load-bearing safety invariant: state-changers default to Review.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void EvaluateFor_AutoApproveRule_StateChange_NoOptIn_ForcesRequiresApproval()
    {
        // Tier=Autonomous, rule says AutoApprove for Low, but the proposal is a
        // state change AND AllowAutoApproveForStateChange is false (the safety
        // default). The policy must force RequiresApproval.
        var policy = PolicyWith(
            AutonomyLevel.Autonomous,
            new BlastRadiusAutonomyRule(BlastRadius.Low, AutonomyDecision.AutoApprove, AllowAutoApproveForStateChange: false));

        var (decision, reason) = policy.EvaluateFor(
            BlastRadius.Low,
            ChangeTargetKind.GitRepo,
            isStateChange: true);

        Assert.Equal(AutonomyDecision.RequiresApproval, decision);
        Assert.Contains("state-change", reason);
    }

    [Fact]
    public void EvaluateFor_AutoApproveRule_StateChange_WithOptIn_AllowsAutoApprove()
    {
        var policy = PolicyWith(
            AutonomyLevel.Autonomous,
            new BlastRadiusAutonomyRule(BlastRadius.Low, AutonomyDecision.AutoApprove, AllowAutoApproveForStateChange: true));

        var (decision, _) = policy.EvaluateFor(
            BlastRadius.Low,
            ChangeTargetKind.GitRepo,
            isStateChange: true);

        Assert.Equal(AutonomyDecision.AutoApprove, decision);
    }

    [Fact]
    public void EvaluateFor_AutoApproveRule_NonStateChange_AllowsAutoApprove()
    {
        // The opt-in is irrelevant for non-state-changes; the rule applies directly.
        var policy = PolicyWith(
            AutonomyLevel.Autonomous,
            new BlastRadiusAutonomyRule(BlastRadius.Low, AutonomyDecision.AutoApprove, AllowAutoApproveForStateChange: false));

        var (decision, _) = policy.EvaluateFor(
            BlastRadius.Low,
            ChangeTargetKind.GitRepo,
            isStateChange: false);

        Assert.Equal(AutonomyDecision.AutoApprove, decision);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 5. Critical-always-requires-approval cannot be loosened by ANY rule.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void EvaluateFor_CriticalBlastRadius_EvenWithAutoApproveRuleAndOptIn_RequiresApproval()
    {
        var policy = PolicyWith(
            AutonomyLevel.Autonomous,
            new BlastRadiusAutonomyRule(BlastRadius.Critical, AutonomyDecision.AutoApprove, AllowAutoApproveForStateChange: true));

        var (decision, reason) = policy.EvaluateFor(
            BlastRadius.Critical,
            ChangeTargetKind.IacDeployment,
            isStateChange: false);

        Assert.Equal(AutonomyDecision.RequiresApproval, decision);
        Assert.Contains("Critical", reason);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 6. Forbidden decision is always honoured.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void EvaluateFor_ForbiddenRule_ReturnsForbiddenRegardlessOfStateChange()
    {
        var policy = PolicyWith(
            AutonomyLevel.Autonomous,
            new BlastRadiusAutonomyRule(BlastRadius.High, AutonomyDecision.Forbidden));

        var (decision, _) = policy.EvaluateFor(
            BlastRadius.High,
            ChangeTargetKind.IacDeployment,
            isStateChange: true);

        Assert.Equal(AutonomyDecision.Forbidden, decision);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 7. Reason string includes useful diagnostics.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void EvaluateFor_ReasonMentionsTierAndRadius()
    {
        var policy = PolicyWith(AutonomyLevel.Supervised);

        var (_, reason) = policy.EvaluateFor(
            BlastRadius.Medium,
            ChangeTargetKind.GitRepo,
            isStateChange: false);

        Assert.Contains("Supervised", reason);
        Assert.Contains("Medium", reason);
    }
}
