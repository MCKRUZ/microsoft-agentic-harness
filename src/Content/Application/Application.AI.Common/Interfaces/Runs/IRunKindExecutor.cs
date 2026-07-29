using Domain.AI.Runs;
using Domain.Common;

namespace Application.AI.Common.Interfaces.Runs;

/// <summary>
/// Performs the work for one <see cref="RunKind"/>. Registered as a keyed service under that kind,
/// which is what makes adding a kind a registration rather than a change to the dispatcher.
/// </summary>
/// <remarks>
/// Implementations receive a run that has already been claimed, so they must not re-check the queue
/// or re-arm anything. They are responsible only for doing the work and reporting how it ended; the
/// dispatcher owns recording that outcome against the run.
/// </remarks>
public interface IRunKindExecutor
{
    /// <summary>Executes a claimed run.</summary>
    /// <param name="record">The claimed run, carrying its target, owner and capability envelope.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>
    /// Success when the work completed, or a failure whose message is safe to hand back to the caller.
    /// </returns>
    Task<Result> ExecuteAsync(RunRecord record, CancellationToken cancellationToken);
}
