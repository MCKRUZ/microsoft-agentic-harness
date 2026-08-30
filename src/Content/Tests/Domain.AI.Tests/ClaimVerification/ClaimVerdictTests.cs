using Domain.AI.ClaimVerification;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.ClaimVerification;

/// <summary>
/// Proves <see cref="ClaimVerdict"/>'s fail-safe shape: confidence is floored only for a genuine
/// finding (<see cref="ClaimVerificationOutcome.Broken"/>, <see cref="ClaimVerificationOutcome.LocationNotFound"/>)
/// and left untouched for every fail-safe outcome — "a failed claim is revised, not deleted," and a
/// claim that was never really checked is not revised at all.
/// </summary>
public sealed class ClaimVerdictTests
{
    private static readonly ClaimConsequenceSignals Signals = new() { CausesWrite = false, GatesADecision = true };

    private static readonly Claim SampleClaim = new()
    {
        Text = "the claim",
        Location = "file:Foo.cs",
        Confidence = 0.9,
        ConsequenceSignals = Signals
    };

    [Fact]
    public void Held_LeavesConfidenceUnchanged()
    {
        var verdict = ClaimVerdict.Held(SampleClaim);

        verdict.Outcome.Should().Be(ClaimVerificationOutcome.Held);
        verdict.Explanation.Should().BeNull();
        verdict.RevisedClaim.Confidence.Should().Be(0.9);
    }

    [Fact]
    public void Broken_FloorsConfidenceToOneTenth()
    {
        var verdict = ClaimVerdict.Broken(SampleClaim, "contradicted");

        verdict.Outcome.Should().Be(ClaimVerificationOutcome.Broken);
        verdict.Explanation.Should().Be("contradicted");
        verdict.RevisedClaim.Confidence.Should().Be(0.1);
    }

    // The one outcome that must NOT be fail-safe-silent — a nonexistent location is a real finding,
    // not "we couldn't check," so it gets the same floor as Broken.
    [Fact]
    public void LocationNotFound_FloorsConfidenceToOneTenth()
    {
        var verdict = ClaimVerdict.LocationNotFound(SampleClaim, "does not exist");

        verdict.Outcome.Should().Be(ClaimVerificationOutcome.LocationNotFound);
        verdict.RevisedClaim.Confidence.Should().Be(0.1);
    }

    [Fact]
    public void Unverifiable_LeavesConfidenceUnchanged()
    {
        var verdict = ClaimVerdict.Unverifiable(SampleClaim, "no reader for scheme");

        verdict.Outcome.Should().Be(ClaimVerificationOutcome.Unverifiable);
        verdict.RevisedClaim.Confidence.Should().Be(0.9);
    }

    [Fact]
    public void VerifierError_LeavesConfidenceUnchanged()
    {
        var verdict = ClaimVerdict.VerifierError(SampleClaim, "timed out");

        verdict.Outcome.Should().Be(ClaimVerificationOutcome.VerifierError);
        verdict.RevisedClaim.Confidence.Should().Be(0.9);
    }

    [Fact]
    public void NotConsequential_LeavesConfidenceUnchanged()
    {
        var verdict = ClaimVerdict.NotConsequential(SampleClaim);

        verdict.Outcome.Should().Be(ClaimVerificationOutcome.NotConsequential);
        verdict.Explanation.Should().BeNull();
        verdict.RevisedClaim.Confidence.Should().Be(0.9);
    }

    // The revise-don't-delete rule, made concrete: a failed claim is a NEW record with the same
    // identity (Text/Location), never absent from whatever list it came from.
    [Fact]
    public void Broken_RevisedClaim_KeepsTextAndLocationIdentity()
    {
        var verdict = ClaimVerdict.Broken(SampleClaim, "contradicted");

        verdict.RevisedClaim.Text.Should().Be(SampleClaim.Text);
        verdict.RevisedClaim.Location.Should().Be(SampleClaim.Location);
    }
}
