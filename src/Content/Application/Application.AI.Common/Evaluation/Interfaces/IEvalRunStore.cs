using Application.AI.Common.Evaluation.Models;
using Domain.AI.Evaluation;

namespace Application.AI.Common.Evaluation.Interfaces;

/// <summary>
/// Durable store for <see cref="EvalRunReport"/>s ingested by the dashboard
/// (Sub-phase 5.4). Provides append-with-idempotency on the natural key
/// (<see cref="EvalRunReport.RunId"/>), the list-view summary projection,
/// and the per-run detail load.
/// </summary>
/// <remarks>
/// <para>
/// Implementations MUST be append-with-idempotency at the public surface:
/// re-appending the same <c>RunId</c> is a no-op and returns <c>false</c>;
/// the original row is not overwritten. Run reports are immutable artifacts.
/// </para>
/// <para>
/// Implementations should be safe for concurrent appends — the CLI may
/// ingest one run while another is mid-write. Query methods need not be
/// transactional but must return a consistent snapshot of any single run.
/// </para>
/// </remarks>
public interface IEvalRunStore
{
    /// <summary>
    /// Persists the supplied report. Returns <c>true</c> when a new row was
    /// written, <c>false</c> when a row with the same
    /// <see cref="EvalRunReport.RunId"/> already exists (idempotent re-ingest).
    /// </summary>
    /// <param name="report">The report to persist. Must not be null.</param>
    /// <param name="receivedAtUtc">
    /// UTC timestamp the dashboard received this report. Stored alongside the
    /// run's own <c>CompletedAtUtc</c> so latency between completion and ingest
    /// is observable (e.g. CI runs ingested minutes after the fact).
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<bool> AppendAsync(
        EvalRunReport report,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the most recent runs in descending order of
    /// <see cref="EvalRunReport.StartedAtUtc"/>, projected to
    /// <see cref="EvalRunSummary"/> so list pages don't pay for per-case payloads.
    /// </summary>
    /// <param name="take">Maximum rows to return. Must be positive.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<IReadOnlyList<EvalRunSummary>> GetRecentAsync(int take, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the full per-case detail for a previously-ingested run, or
    /// <c>null</c> when the run is unknown.
    /// </summary>
    /// <param name="runId">Natural identifier of the run.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<EvalRunReport?> GetRunDetailAsync(string runId, CancellationToken cancellationToken);
}
