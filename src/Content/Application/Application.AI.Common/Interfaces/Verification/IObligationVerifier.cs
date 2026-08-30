using Domain.AI.Verification;

namespace Application.AI.Common.Interfaces.Verification;

/// <summary>
/// Checks one <see cref="Obligation"/> against its cited location and returns a
/// <see cref="VerificationVerdict"/>. A conforming implementation reads
/// <see cref="Obligation.ReliesOn"/> directly rather than judging plausibility from context it
/// already had — that is what makes the resulting verdict evidence, not a second opinion.
/// </summary>
/// <remarks>
/// <para>
/// Implementations are dispatched one obligation at a time by <c>ObligationVerificationRunner</c>,
/// which owns fan-out, the per-verifier timeout, and converting any exception this method throws
/// into <see cref="VerificationVerdict.VerifierError"/> — an implementation does not need to catch
/// its own failures for that reason, only for cases where it can produce a more specific
/// <see cref="VerificationOutcome.Unverifiable"/> reason than a generic error would. An
/// implementation that returns a soft-fail result from its own model call (rather than throwing)
/// must still map that to <see cref="VerificationVerdict.VerifierError"/> itself — the runner's
/// catch only sees exceptions.
/// </para>
/// <para>
/// The artifact content passed to <see cref="VerifyAsync"/> is the same artifact
/// <c>IObligationExtractor</c> read the obligation from — Package E's scope is single-artifact
/// verification, so "the other location" an obligation relies on is resolved within this same
/// text, not fetched from elsewhere. A multi-artifact/external-resource verifier is a distinct,
/// larger capability (#319), built as a separate implementation of this interface rather than an
/// extension of this one.
/// </para>
/// </remarks>
public interface IObligationVerifier
{
    /// <summary>
    /// Checks <paramref name="obligation"/> against <paramref name="artifactContent"/> — the
    /// artifact's own text, untrusted — and returns the resulting verdict.
    /// </summary>
    Task<VerificationVerdict> VerifyAsync(Obligation obligation, string artifactContent, CancellationToken cancellationToken);
}
