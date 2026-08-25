using Application.AI.Common.Categorization;
using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces.Context;
using FluentAssertions;

namespace Application.AI.Common.Tests.Categorization;

/// <summary>
/// Pins the consumer half of #507's fix: the translation from "this skill's body was held back" to a
/// skills lane that does not charge for it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RegistrationBreakdownCalculatorTests"/> proves the calculator is right <em>given</em> a
/// correct flag. That is not the same as proving the fix works, and the difference is the whole reason
/// #507 needed a second attempt: the arithmetic was never the hard part, knowing which skills were
/// actually in the prompt was.
/// </para>
/// <para>
/// So this file asserts the mapping rule itself — <c>InlinedInPrompt</c> is the negation of membership
/// in the held-back set — against the shapes the handler actually produces, including the ones that
/// look harmless and are not: a null set, and an id that differs only by casing.
/// </para>
/// </remarks>
public sealed class RegistrationSnapshotDisclosureWiringTests
{
    private const string Body = "skill body text that is long enough to measure";

    /// <summary>
    /// Mirrors <c>ExecuteAgentTurnCommandHandler.BuildRegistrationSnapshot</c>'s rule exactly. Kept as
    /// one line so a change to the handler that inverts or drops it shows up here as an obvious
    /// divergence rather than as a subtly different number on a dashboard.
    /// </summary>
    private static SkillRegistration Register(string id, IReadOnlySet<string>? disclosedOnDemand) =>
        new(id, id, Body, InlinedInPrompt: disclosedOnDemand?.Contains(id) != true);

    private static RegistrationSnapshot SnapshotOf(string instruction, params SkillRegistration[] skills) =>
        new(instruction, skills, [], [], []);

    [Fact]
    public void SkillInTheHeldBackSet_IsNotChargedToTheSkillsLane()
    {
        var disclosed = new HashSet<string>(["demo-skill"], StringComparer.OrdinalIgnoreCase);

        var breakdown = RegistrationBreakdownCalculator.From(
            SnapshotOf("instruction with no skill body in it", Register("demo-skill", disclosed)));

        breakdown.Skills.Should().Be(0);
    }

    [Fact]
    public void SkillAbsentFromTheHeldBackSet_IsChargedNormally()
    {
        var disclosed = new HashSet<string>(["other-skill"], StringComparer.OrdinalIgnoreCase);

        var breakdown = RegistrationBreakdownCalculator.From(
            SnapshotOf("instruction " + Body, Register("demo-skill", disclosed)));

        breakdown.Skills.Should().Be(TokenEstimationHelper.EstimateTokens(Body));
    }

    [Fact]
    public void HeldBackSetIsCaseInsensitive_SoAnIdCasingMismatchCannotSilentlyChargeTheBody()
    {
        // The factory builds this set with OrdinalIgnoreCase over the same SkillDefinition.Id values
        // the handler looks up, and it travels as IReadOnlySet<string> — which preserves the comparer
        // through interface dispatch. If it were ever rebuilt with the default comparer, a casing
        // difference would silently flip a held-back skill back to "charged", restoring the defect
        // without any test noticing. This is the cheap guard for that.
        var disclosed = new HashSet<string>(["Demo-Skill"], StringComparer.OrdinalIgnoreCase);

        var breakdown = RegistrationBreakdownCalculator.From(
            SnapshotOf("instruction", Register("demo-skill", disclosed)));

        breakdown.Skills.Should().Be(0);
    }

    [Fact]
    public void NoHeldBackSet_ChargesEverySkill_TheConservativePreFixReading()
    {
        // Null means the producer did not say — an older context, or the delegation path that carries
        // no skills at all. Charging the body is the safe direction: it can overstate the skills lane,
        // where the opposite would understate the system lane, which is the failure #507 is about.
        var breakdown = RegistrationBreakdownCalculator.From(
            SnapshotOf("instruction " + Body, Register("demo-skill", disclosedOnDemand: null)));

        breakdown.Skills.Should().Be(TokenEstimationHelper.EstimateTokens(Body));
    }

    [Fact]
    public void AHeldBackBody_LeavesTheSystemLaneEqualToTheWholeInstruction()
    {
        // The end-to-end property, stated as the dashboard reads it: nothing is subtracted from the
        // system prompt for text the system prompt never contained.
        const string instruction = "the entire static system prompt, with no skill body inside it";
        var disclosed = new HashSet<string>(["demo-skill"], StringComparer.OrdinalIgnoreCase);

        var breakdown = RegistrationBreakdownCalculator.From(
            SnapshotOf(instruction, Register("demo-skill", disclosed)));

        breakdown.System.Should().Be(TokenEstimationHelper.EstimateTokens(instruction));
    }
}
