namespace Application.AI.Common.Evaluation;

/// <summary>
/// Picks the single sample closest to an already-computed aggregate score, so a forensic
/// field (a judge's cited clause, its raw output) can be carried whole from one real call
/// rather than synthesized from a mix of several.
/// </summary>
/// <remarks>
/// Shared by <c>EvalRunner</c>'s median-across-repeats aggregation and
/// <c>JuryLlmJudge</c>'s panel aggregation — both reduce several judge-produced samples to
/// one score and both need a single representative sample's other fields to go with it.
/// </remarks>
public static class RepresentativeSelector
{
    /// <summary>
    /// Returns the element of <paramref name="samples"/> whose <paramref name="score"/> is
    /// closest to <paramref name="target"/>. Ties resolve to the first element encountered
    /// (a stable, deterministic choice). The single-element case short-circuits without
    /// allocating a comparer delegate.
    /// </summary>
    public static T PickClosest<T>(IReadOnlyList<T> samples, Func<T, double> score, double target)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(score);
        if (samples.Count == 0)
        {
            throw new ArgumentException("Must contain at least one sample.", nameof(samples));
        }

        return samples.Count == 1
            ? samples[0]
            : samples.MinBy(s => Math.Abs(score(s) - target))!;
    }
}
