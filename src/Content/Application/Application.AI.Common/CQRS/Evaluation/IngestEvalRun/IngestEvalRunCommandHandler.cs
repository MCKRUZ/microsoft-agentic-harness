using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Evaluation.Models;
using Domain.AI.Evaluation;
using Domain.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.CQRS.Evaluation.IngestEvalRun;

/// <summary>
/// Handles <see cref="IngestEvalRunCommand"/> by persisting the supplied
/// <see cref="Domain.AI.Evaluation.EvalRunReport"/> through
/// <see cref="IEvalRunStore.AppendAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// ReceivedAt timestamp is taken from <see cref="TimeProvider"/> (not from the
/// caller) so the dashboard's notion of "when did we receive this?" is owned by
/// the server, not spoofable by the ingest client. Distinct from the report's own
/// <see cref="Domain.AI.Evaluation.EvalRunReport.CompletedAtUtc"/> which the run
/// itself recorded.
/// </para>
/// <para>
/// Store failures other than <see cref="OperationCanceledException"/> are
/// translated into <see cref="Result{T}.Fail(string[])"/> carrying the stable,
/// scrubbed <see cref="PersistFailedCode"/> — the raw exception is logged via
/// structured logging only and never returned to the caller. Successful ingest
/// returns the <see cref="IngestEvalRunResult"/> with <c>Inserted</c> matching
/// the store's idempotency outcome.
/// </para>
/// </remarks>
public sealed class IngestEvalRunCommandHandler
    : IRequestHandler<IngestEvalRunCommand, Result<IngestEvalRunResult>>
{
    /// <summary>
    /// Stable, scrubbed failure code returned when the eval-run store throws while persisting.
    /// The raw exception (which can embed absolute file paths, SQLite/EF provider internals,
    /// or connection details) is sent only to structured logging — never to the HTTP-facing
    /// <see cref="Result{T}.Fail(string[])"/> surfaced to the ingest caller, dashboards, or
    /// AG-UI notifications.
    /// </summary>
    public const string PersistFailedCode = "eval.ingest.persist_failed";

    private readonly IEvalRunStore _store;
    private readonly IEvalRunNotifier _notifier;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<IngestEvalRunCommandHandler> _logger;

    /// <summary>Initializes a new instance.</summary>
    public IngestEvalRunCommandHandler(
        IEvalRunStore store,
        IEvalRunNotifier notifier,
        TimeProvider timeProvider,
        ILogger<IngestEvalRunCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(notifier);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _notifier = notifier;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<IngestEvalRunResult>> Handle(
        IngestEvalRunCommand request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var receivedAtUtc = _timeProvider.GetUtcNow();

        bool inserted;
        try
        {
            inserted = await _store
                .AppendAsync(request.Report, receivedAtUtc, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to persist eval run {RunId} into the dashboard store.",
                request.Report.RunId);
            return Result<IngestEvalRunResult>.Fail(PersistFailedCode);
        }

        if (inserted)
        {
            _logger.LogInformation(
                "Ingested eval run {RunId} (started {StartedAt}, verdict {Verdict}, {Cases} cases).",
                request.Report.RunId,
                request.Report.StartedAtUtc,
                request.Report.OverallVerdict,
                request.Report.Results.Count);

            await NotifyAsync(request.Report, receivedAtUtc, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _logger.LogDebug(
                "Eval run {RunId} already present in dashboard store; ingest is a no-op.",
                request.Report.RunId);
        }

        return Result<IngestEvalRunResult>.Success(new IngestEvalRunResult
        {
            RunId = request.Report.RunId,
            Inserted = inserted,
            ReceivedAtUtc = receivedAtUtc,
        });
    }

    private async Task NotifyAsync(
        EvalRunReport report,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken)
    {
        // Best-effort fan-out. Notifier-failure must not corrupt the success outcome
        // that the store has already committed; log + swallow per the IEvalRunNotifier
        // contract. OCE still propagates so genuine cancellation isn't masked.
        var summary = new EvalRunSummary
        {
            RunId = report.RunId,
            StartedAtUtc = report.StartedAtUtc,
            CompletedAtUtc = report.CompletedAtUtc,
            Duration = report.Duration,
            PassedCount = report.PassedCount,
            FailedCount = report.FailedCount,
            WarnedCount = report.WarnedCount,
            ErroredCount = report.ErroredCount,
            TotalCostUsd = report.TotalCostUsd,
            Repeats = report.Repeats,
            OverallVerdict = report.OverallVerdict,
            ReceivedAtUtc = receivedAtUtc,
        };

        try
        {
            await _notifier.NotifyRunCompletedAsync(summary, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to notify subscribers of eval run {RunId}; ingest succeeded regardless.",
                report.RunId);
        }
    }
}
