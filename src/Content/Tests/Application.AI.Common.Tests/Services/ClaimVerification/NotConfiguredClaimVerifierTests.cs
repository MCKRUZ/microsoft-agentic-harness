using Application.AI.Common.Services.ClaimVerification;
using Domain.AI.ClaimVerification;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Application.AI.Common.Tests.Services.ClaimVerification;

public sealed class NotConfiguredClaimVerifierTests
{
    private readonly NotConfiguredClaimVerifier _sut = new(NullLogger<NotConfiguredClaimVerifier>.Instance);

    [Fact]
    public async Task VerifyAsync_AnyClaim_ReturnsUnverifiableNotThrow()
    {
        var claim = new Claim
        {
            Text = "some claim",
            Location = "file:Foo.cs",
            ConsequenceSignals = new ClaimConsequenceSignals { CausesWrite = false, GatesADecision = true }
        };

        var verdict = await _sut.VerifyAsync(claim, "evidence", CancellationToken.None);

        verdict.Outcome.Should().Be(ClaimVerificationOutcome.Unverifiable);
        verdict.RevisedClaim.Should().Be(claim);
    }
}
