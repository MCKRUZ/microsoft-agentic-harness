using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Runs;
using Domain.Common;
using Domain.Common.Config;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.Core.CQRS.Evaluation.Runs;

/// <summary>
/// Handles <see cref="CancelEvalRunCommand"/>: stops an evaluation run that has not started executing.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Simpler than the workflow cancel path, because an evaluation holds less.</strong> A workflow
/// can be parked on a human gate whose approval has to be withdrawn, and can be signalled mid-flight
/// through the plan cancellation registry. An evaluation parks on nothing and has no such registry, so
/// what is left is the transition itself.
/// </para>
/// <para>
/// Authorized exactly like reading the run: another caller's run, a job that never existed, and a run
/// of another kind are one answer. A distinguishable response would let a caller discover work it was
/// not given the identifier for — and here it would also let them stop it.
/// </para>
/// </remarks>
public sealed class CancelEvalRunCommandHandler
    : IRequestHandler<CancelEvalRunCommand, Result<CancelEvalRunResult>>
{
    private readonly IRunJobStore _runStore;
    private readonly IRunProgressBroker _progress;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly TimeProvider _time;
    private readonly ILogger<CancelEvalRunCommandHandler> _logger;

    /// <summary>Initializes a new <see cref="CancelEvalRunCommandHandler"/>.</summary>
    public CancelEvalRunCommandHandler(
        IRunJobStore runStore,
        IRunProgressBroker progress,
        IOptionsMonitor<AppConfig> config,
        TimeProvider time,
        ILogger<CancelEvalRunCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(runStore);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _runStore = runStore;
        _progress = progress;
        _config = config;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<Result<CancelEvalRunResult>> Handle(
        CancelEvalRunCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_config.CurrentValue.AI.Evaluation.Enabled)
        {
            return Task.FromResult(Result<CancelEvalRunResult>.Forbidden(
                "Evaluation is disabled. Set AppConfig.AI.Evaluation.Enabled = true to enable it."));
        }

        var record = _runStore.Get(request.JobId, request.OwnerId, request.TenantId);

        if (record is null || record.Kind != RunKind.Evaluation)
            return Task.FromResult(Result<CancelEvalRunResult>.NotFound($"No run {request.JobId} found."));

        if (record.IsTerminal)
        {
            return Task.FromResult(Result<CancelEvalRunResult>.Conflict(
                $"Run {request.JobId} has already finished and cannot be cancelled."));
        }

        var stopped = _runStore.TryCancel(request.JobId, _time.GetUtcNow());
        if (stopped is null)
        {
            // Executing, or it reached a terminal state between the read above and here. Either way its
            // status belongs to the dispatch holding it. Reported truthfully rather than as a success:
            // a caller told the run had stopped would assume the spend had stopped with it.
            return Task.FromResult(Result<CancelEvalRunResult>.Success(
                new CancelEvalRunResult { Stopped = false }));
        }

        // Published here because nothing else will: no dispatch is going to run for this job, and the
        // terminal event is what ends a watcher's stream. Without it, anyone streaming a queued run
        // holds the connection and a stream slot until they give up.
        _progress.Publish(
            stopped.JobId,
            RunProgressKind.RunFinished,
            status: nameof(RunStatus.Cancelled),
            detail: "The run was cancelled by its owner.");

        _logger.LogInformation("Evaluation run {JobId} was cancelled by its owner.", stopped.JobId);

        return Task.FromResult(Result<CancelEvalRunResult>.Success(
            new CancelEvalRunResult { Stopped = true }));
    }
}
