using Domain.AI.ClaimVerification;

namespace Application.AI.Common.Interfaces.ClaimVerification;

/// <summary>
/// Compares one <see cref="Claim"/> against evidence already fetched from the location it cites,
/// and returns a <see cref="ClaimVerdict"/>. A conforming implementation judges the claim against
/// the evidence text as given — it does not know or care which <c>ILocatedArtifactReader</c> scheme
/// produced it.
/// </summary>
/// <remarks>
/// Implementations are dispatched one claim at a time by <c>ClaimVerificationRunner</c>, which owns
/// consequence classification, reader resolution/dispatch, the per-verifier timeout, and converting
/// any exception this method throws into <see cref="ClaimVerdict.VerifierError"/> — an
/// implementation does not need to catch its own failures for that reason, only for cases where it
/// can produce a more specific <see cref="ClaimVerificationOutcome.Unverifiable"/> reason than a
/// generic error would. An implementation that returns a soft-fail result from its own model call
/// (rather than throwing) must still map that to <see cref="ClaimVerdict.VerifierError"/> itself —
/// the runner's catch only sees exceptions.
/// </remarks>
public interface IClaimVerifier
{
    /// <summary>
    /// Checks <paramref name="claim"/> against <paramref name="evidenceContent"/> — the text already
    /// read from <see cref="Claim.Location"/>, untrusted — and returns the resulting verdict.
    /// </summary>
    Task<ClaimVerdict> VerifyAsync(Claim claim, string evidenceContent, CancellationToken cancellationToken);
}
