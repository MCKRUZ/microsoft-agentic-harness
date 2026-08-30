using Domain.AI.Verification;
using Domain.Common;

namespace Application.AI.Common.Interfaces.Verification;

/// <summary>
/// Extracts obligations from an artifact: for each specific reliance the artifact's content creates
/// on some other location, one <see cref="Obligation"/> naming both ends and what must hold between
/// them. Asking for obligations rather than "problems" is the whole design — it forces a located,
/// checkable claim instead of a plausible-sounding one.
/// </summary>
/// <remarks>
/// Returns <see cref="Result{T}"/>, not a bare list, so an extraction failure is distinguishable
/// from a genuine "this artifact has nothing to check" outcome — both would otherwise collapse to
/// an empty list, and a caller could not tell a broken pipeline from a clean artifact. Malformed
/// model output must surface as a failed <see cref="Result{T}"/>; it must never be reported as
/// <c>Result&lt;IReadOnlyList&lt;Obligation&gt;&gt;.Success([])</c>.
/// </remarks>
public interface IObligationExtractor
{
    /// <summary>
    /// Extracts obligations from <paramref name="artifactContent"/>. <paramref name="artifactPath"/>
    /// identifies the artifact (e.g. a file path) so extracted <see cref="Obligation.Where"/> values
    /// can be located, not just described. A successful result with an empty list means the
    /// extraction ran and found nothing to check — a valid, common outcome, not a failure.
    /// </summary>
    Task<Result<IReadOnlyList<Obligation>>> ExtractAsync(
        string artifactPath, string artifactContent, CancellationToken cancellationToken);
}
