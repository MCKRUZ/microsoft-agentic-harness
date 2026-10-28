using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Runs;
using Application.AI.Common.Services.Runs;
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
/// It still goes through <see cref="RunCancellation"/>, the one cancel path every caller shares (#992), so an evaluation
/// is stopped exactly as erasure stops it. For an evaluation the withdrawal finds nothing to withdraw, and no registry is
/// passed, so nothing is signalled.
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
    private readonly IEscalationService _escalations;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly TimeProvider _time;
    private readonly ILogger<CancelEvalRunCommandHandler> _logger;

    /// <summary>Initializes a new <see cref="CancelEvalRunCommandHandler"/>.</summary>
    public CancelEvalRunCommandHandler(
        IRunJobStore runStore,
        IRunProgressBroker progress,
        IEscalationService escalations,
        IOptionsMonitor<AppConfig> config,
        TimeProvider time,
        ILogger<CancelEvalRunCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(runStore);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(escalations);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _runStore = runStore;
        _progress = progress;
        _escalations = escalations;
        _config = config;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<CancelEvalRunResult>> Handle(
        CancelEvalRunCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_config.CurrentValue.AI.Evaluation.Enabled)
        {
            return Result<CancelEvalRunResult>.Forbidden(
                "Evaluation is disabled. Set AppConfig.AI.Evaluation.Enabled = true to enable it.");
        }

        var record = _runStore.Get(request.JobId, request.OwnerId, request.TenantId);

        if (record is null || record.Kind != RunKind.Evaluation)
            return Result<CancelEvalRunResult>.NotFound($"No run {request.JobId} found.");

        if (record.IsTerminal)
        {
            return Result<CancelEvalRunResult>.Conflict(
                $"Run {request.JobId} has already finished and cannot be cancelled.");
        }

        var outcome = await RunCancellation.CancelAsync(
            _runStore,
            _progress,
            _escalations,

            // Nothing to signal: an evaluation that has started runs to its end.
            registry: null,
            _logger,
            record,
            new RunCancellationNotice(
                _time.GetUtcNow(),
                "The run was cancelled by its owner.",
                "The run this approval was raised for was cancelled.",
                request.OwnerId)).ConfigureAwait(false);

        // Executing, or it reached a terminal state between the read above and here. Either way its status belongs to
        // the dispatch holding it. Reported truthfully rather than as a success: a caller told the run had stopped would
        // assume the spend had stopped with it.
        if (!outcome.StoppedImmediately)
            return Result<CancelEvalRunResult>.Success(new CancelEvalRunResult { Stopped = false });

        _logger.LogInformation("Evaluation run {JobId} was cancelled by its owner.", record.JobId);

        return Result<CancelEvalRunResult>.Success(new CancelEvalRunResult { Stopped = true });
    }
}
