using Application.AI.Common.Interfaces.Evaluation;
using Application.AI.Common.Interfaces.Runs;

namespace Infrastructure.AI.Runs;

/// <summary>
/// Releases an evaluation run's stored request and report when its record is reclaimed.
/// </summary>
/// <remarks>
/// <para>
/// A named type rather than a factory registration, for the same reason
/// <see cref="RunProgressReclaimListener"/> is one: <c>TryAddEnumerable</c> distinguishes
/// registrations by implementation type and rejects factory descriptors, so a lambda resolving the
/// store would either throw at composition or — registered with plain <c>AddSingleton</c> — register
/// again every time the substrate is composed. The run substrate is wired with <c>TryAdd</c>
/// throughout precisely so composing it twice is harmless; this keeps that true.
/// </para>
/// <para>
/// The store already implements <see cref="IRunReclaimListener"/> — that is deliberate, so every
/// implementation has to answer the reclaim question rather than leave it to whoever registers it.
/// This delegates to that implementation rather than duplicating it.
/// </para>
/// </remarks>
/// <param name="submissions">The store whose per-run entries this releases.</param>
public sealed class EvalSubmissionReclaimListener(IEvalRunSubmissionStore submissions) : IRunReclaimListener
{
    private readonly IEvalRunSubmissionStore _submissions =
        submissions ?? throw new ArgumentNullException(nameof(submissions));

    /// <inheritdoc />
    public void OnRunsReclaimed(IReadOnlyList<string> jobIds) => _submissions.OnRunsReclaimed(jobIds);
}
