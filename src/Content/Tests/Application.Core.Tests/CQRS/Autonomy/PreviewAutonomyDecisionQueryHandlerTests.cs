using Application.AI.Common.Interfaces.Governance;
using Application.Core.CQRS.Autonomy;
using Application.Core.Permissions;
using Domain.AI.Agents;
using Domain.AI.Changes;
using Domain.AI.Governance;
using Domain.Common;
using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.Core.Tests.CQRS.Autonomy;

/// <summary>
/// Tests for <see cref="PreviewAutonomyDecisionQueryHandler"/>. The core property is parity:
/// the preview runs the <em>same</em> <see cref="AutonomyDecisionEvaluator"/> the enforcement
/// path uses, so for identical inputs the preview must return the identical decision. Also
/// covers the 404/400 classification of malformed inputs and the zero-side-effect guarantee.
/// </summary>
public sealed class PreviewAutonomyDecisionQueryHandlerTests
{
    private static AutonomyDecisionEvaluator BuildRealEvaluator(
        AppConfig config, string environmentName = "Development")
    {
        var monitor = Mock.Of<IOptionsMonitor<AppConfig>>(o => o.CurrentValue == config);
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(environmentName);
        return new AutonomyDecisionEvaluator(
            monitor, env.Object, NullLogger<AutonomyDecisionEvaluator>.Instance);
    }

    private static PreviewAutonomyDecisionQueryHandler BuildHandler(
        IAutonomyDecisionEvaluator evaluator, AutonomyLevel tier)
    {
        var resolver = new Mock<IAutonomyTierResolver>();
        resolver.Setup(r => r.Resolve(It.IsAny<SubagentType>())).Returns(tier);
        return new PreviewAutonomyDecisionQueryHandler(
            resolver.Object, evaluator, NullLogger<PreviewAutonomyDecisionQueryHandler>.Instance);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Parity: preview == enforcement-path evaluator, same inputs, both with
    // graded autonomy disabled (PR-2 fallback) and enabled (layered rules).
    // ─────────────────────────────────────────────────────────────────────

    public static TheoryData<bool, AutonomyLevel, BlastRadius, bool> ParityCases() => new()
    {
        { false, AutonomyLevel.Restricted, BlastRadius.Trivial, false },
        { false, AutonomyLevel.Supervised, BlastRadius.Medium, true },
        { false, AutonomyLevel.Autonomous, BlastRadius.Critical, false },
        { true, AutonomyLevel.Restricted, BlastRadius.Low, false },
        { true, AutonomyLevel.Supervised, BlastRadius.High, true },
        { true, AutonomyLevel.Autonomous, BlastRadius.Trivial, false },
        { true, AutonomyLevel.Autonomous, BlastRadius.Medium, true },
    };

    [Theory]
    [MemberData(nameof(ParityCases))]
    public async Task Handle_SameInputsAsEnforcementEvaluator_ReturnsIdenticalDecision(
        bool gradedEnabled, AutonomyLevel tier, BlastRadius radius, bool isStateChange)
    {
        var config = new AppConfig();
        config.AI.Permissions.GradedAutonomy.Enabled = gradedEnabled;
        var evaluator = BuildRealEvaluator(config);

        // What the enforcement path would decide for these exact inputs.
        var expected = evaluator.Evaluate(
            tier, radius, ChangeTargetKind.GitRepo, isStateChange, "skill.demo");

        var handler = BuildHandler(evaluator, tier);
        var result = await handler.Handle(
            new PreviewAutonomyDecisionQuery
            {
                SubagentType = nameof(SubagentType.Execute),
                BlastRadius = radius.ToString(),
                TargetKind = nameof(ChangeTargetKind.GitRepo),
                IsStateChange = isStateChange,
                SkillKey = "skill.demo",
            },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var preview = result.Value!;
        preview.Decision.Should().Be(expected.Decision,
            "the preview must never drift from the enforcement path's decision");
        preview.Tier.Should().Be(expected.Tier);
        preview.BlastRadius.Should().Be(expected.BlastRadius);
        preview.TargetKind.Should().Be(expected.TargetKind);
        preview.IsStateChange.Should().Be(expected.IsStateChange);
        preview.Environment.Should().Be(expected.Environment);
        preview.SkillKey.Should().Be(expected.SkillKey);
        preview.Reason.Should().Be(expected.Reason);
        preview.SubagentType.Should().Be(SubagentType.Execute);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Input classification: unknown subagent → NotFound; malformed enum
    // names → ValidationFailure.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_UnknownSubagentType_ReturnsNotFoundWithoutEvaluating()
    {
        var resolver = new Mock<IAutonomyTierResolver>(MockBehavior.Strict);
        var evaluator = new Mock<IAutonomyDecisionEvaluator>(MockBehavior.Strict);
        var handler = new PreviewAutonomyDecisionQueryHandler(
            resolver.Object, evaluator.Object,
            NullLogger<PreviewAutonomyDecisionQueryHandler>.Instance);

        var result = await handler.Handle(
            new PreviewAutonomyDecisionQuery
            {
                SubagentType = "Nonexistent",
                BlastRadius = nameof(BlastRadius.Low),
            },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.NotFound);
        resolver.VerifyNoOtherCalls();
        evaluator.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("NotARadius", nameof(ChangeTargetKind.GitRepo))]
    [InlineData("3", nameof(ChangeTargetKind.GitRepo))] // numeric forms rejected
    [InlineData(nameof(BlastRadius.Low), "NotAKind")]
    [InlineData(nameof(BlastRadius.Low), "1")]
    public async Task Handle_MalformedEnumName_ReturnsValidationFailure(
        string blastRadius, string targetKind)
    {
        var resolver = new Mock<IAutonomyTierResolver>(MockBehavior.Strict);
        var evaluator = new Mock<IAutonomyDecisionEvaluator>(MockBehavior.Strict);
        var handler = new PreviewAutonomyDecisionQueryHandler(
            resolver.Object, evaluator.Object,
            NullLogger<PreviewAutonomyDecisionQueryHandler>.Instance);

        var result = await handler.Handle(
            new PreviewAutonomyDecisionQuery
            {
                SubagentType = nameof(SubagentType.Explore),
                BlastRadius = blastRadius,
                TargetKind = targetKind,
            },
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.FailureType.Should().Be(ResultFailureType.Validation);
        resolver.VerifyNoOtherCalls();
        evaluator.VerifyNoOtherCalls();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Side-effect freedom: exactly one Resolve and one Evaluate, nothing else.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_ValidPreview_PerformsExactlyOneResolveAndOneEvaluate()
    {
        var expected = new AutonomyDecisionResult(
            AutonomyDecision.RequiresApproval, AutonomyLevel.Supervised, BlastRadius.Medium,
            ChangeTargetKind.Unspecified, false, "Development", null, "test reason");

        // Strict: any interaction beyond the two expected read calls throws, proving the
        // preview cannot write, audit, or escalate through its dependencies.
        var resolver = new Mock<IAutonomyTierResolver>(MockBehavior.Strict);
        resolver.Setup(r => r.Resolve(SubagentType.Plan)).Returns(AutonomyLevel.Supervised);
        var evaluator = new Mock<IAutonomyDecisionEvaluator>(MockBehavior.Strict);
        evaluator
            .Setup(e => e.Evaluate(
                AutonomyLevel.Supervised, BlastRadius.Medium, ChangeTargetKind.Unspecified,
                false, null))
            .Returns(expected);

        var handler = new PreviewAutonomyDecisionQueryHandler(
            resolver.Object, evaluator.Object,
            NullLogger<PreviewAutonomyDecisionQueryHandler>.Instance);

        var result = await handler.Handle(
            new PreviewAutonomyDecisionQuery
            {
                SubagentType = nameof(SubagentType.Plan),
                BlastRadius = nameof(BlastRadius.Medium),
                SkillKey = "   ", // whitespace-only skill keys normalize to null
            },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Decision.Should().Be(AutonomyDecision.RequiresApproval);
        resolver.Verify(r => r.Resolve(SubagentType.Plan), Times.Once);
        evaluator.Verify(
            e => e.Evaluate(
                AutonomyLevel.Supervised, BlastRadius.Medium, ChangeTargetKind.Unspecified,
                false, null),
            Times.Once);
        resolver.VerifyNoOtherCalls();
        evaluator.VerifyNoOtherCalls();
    }
}
