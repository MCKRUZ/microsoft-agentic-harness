using Domain.AI.Verification;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Verification;

/// <summary>
/// Proves the fail-safe shape the whole feature depends on: <see cref="VerificationOutcome.Held"/>,
/// <see cref="VerificationOutcome.Unverifiable"/>, and <see cref="VerificationOutcome.VerifierError"/>
/// all set <see cref="VerificationVerdict.Holds"/> to <c>true</c>, but remain distinct
/// <see cref="VerificationVerdict.Outcome"/> values — a bare bool could not make that distinction,
/// and collapsing it back to one is the exact defect shape this codebase's history (#490) tracks.
/// </summary>
public sealed class VerificationVerdictTests
{
    private static readonly Obligation SampleObligation = new(Where: "where", ReliesOn: "relies on", Property: "property");

    [Fact]
    public void Held_SetsHoldsTrueAndOutcomeHeld()
    {
        var verdict = VerificationVerdict.Held(SampleObligation);

        verdict.Holds.Should().BeTrue();
        verdict.Outcome.Should().Be(VerificationOutcome.Held);
        verdict.Explanation.Should().BeNull();
    }

    [Fact]
    public void Broken_SetsHoldsFalseAndOutcomeBroken()
    {
        var verdict = VerificationVerdict.Broken(SampleObligation, "it doesn't hold");

        verdict.Holds.Should().BeFalse();
        verdict.Outcome.Should().Be(VerificationOutcome.Broken);
        verdict.Explanation.Should().Be("it doesn't hold");
    }

    [Fact]
    public void Unverifiable_SetsHoldsTrueAndOutcomeUnverifiable()
    {
        var verdict = VerificationVerdict.Unverifiable(SampleObligation, "could not locate reliesOn");

        verdict.Holds.Should().BeTrue();
        verdict.Outcome.Should().Be(VerificationOutcome.Unverifiable);
        verdict.Explanation.Should().Be("could not locate reliesOn");
    }

    [Fact]
    public void VerifierError_SetsHoldsTrueAndOutcomeVerifierError()
    {
        var verdict = VerificationVerdict.VerifierError(SampleObligation, "timed out");

        verdict.Holds.Should().BeTrue();
        verdict.Outcome.Should().Be(VerificationOutcome.VerifierError);
        verdict.Explanation.Should().Be("timed out");
    }

    // The distinguishing assertion: Held and VerifierError agree on Holds but must not be
    // reported as the same thing — telemetry/audit needs to tell "checked, fine" apart from
    // "couldn't check, reporting fine anyway."
    [Fact]
    public void Held_And_VerifierError_ShareHoldsButHaveDistinctOutcomes()
    {
        var held = VerificationVerdict.Held(SampleObligation);
        var errored = VerificationVerdict.VerifierError(SampleObligation, "reason");

        held.Holds.Should().Be(errored.Holds);
        held.Outcome.Should().NotBe(errored.Outcome);
    }
}
