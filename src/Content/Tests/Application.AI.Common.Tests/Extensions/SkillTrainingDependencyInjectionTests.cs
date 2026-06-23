using Application.AI.Common.Extensions;
using Application.AI.Common.Services.SkillTraining;
using Domain.AI.SkillTraining;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Application.AI.Common.Tests.Extensions;

/// <summary>
/// Tests for the editable-surface opt-in on <see cref="SkillTrainingDependencyInjection.AddSkillTrainingDependencies"/> —
/// the only sanctioned, code-level way to widen the Self-Harness fence (Phase 2).
/// </summary>
public class SkillTrainingDependencyInjectionTests
{
    private static EditableSurfaceRegistry ResolveRegistry(IServiceCollection services) =>
        services.BuildServiceProvider().GetRequiredService<EditableSurfaceRegistry>();

    [Fact]
    public void Default_LocksEverythingButSkillDocument()
    {
        var registry = ResolveRegistry(new ServiceCollection().AddSkillTrainingDependencies());

        registry.IsEditable(HarnessSurface.SkillDocument).Should().BeTrue();
        registry.IsEditable(HarnessSurface.FailureRecovery).Should().BeFalse(
            because: "surfaces stay locked by default; widening is an explicit opt-in");
    }

    [Fact]
    public void Widening_UnlocksRequestedSurfaces_AndAlwaysKeepsSkillDocument()
    {
        var registry = ResolveRegistry(new ServiceCollection().AddSkillTrainingDependencies(
            [HarnessSurface.ArtifactGuidance, HarnessSurface.FailureRecovery, HarnessSurface.VerificationPrompt]));

        registry.IsEditable(HarnessSurface.ArtifactGuidance).Should().BeTrue();
        registry.IsEditable(HarnessSurface.FailureRecovery).Should().BeTrue();
        registry.IsEditable(HarnessSurface.VerificationPrompt).Should().BeTrue();
        registry.IsEditable(HarnessSurface.SkillDocument).Should().BeTrue(
            because: "SkillDocument is always editable, even when not explicitly listed");
    }

    [Theory]
    [InlineData(HarnessSurface.DeniedTools)]
    [InlineData(HarnessSurface.AutonomyTier)]
    [InlineData(HarnessSurface.ContentSafetyConfig)]
    [InlineData(HarnessSurface.EditableSurfaceRegistry)]
    public void Widening_WithFrozenByConstructionSurface_ThrowsAtComposition(HarnessSurface frozen)
    {
        var act = () => new ServiceCollection().AddSkillTrainingDependencies([frozen]);

        act.Should().Throw<ArgumentException>(
            because: "governance surfaces can never be unlocked, even by an explicit human opt-in");
    }
}
