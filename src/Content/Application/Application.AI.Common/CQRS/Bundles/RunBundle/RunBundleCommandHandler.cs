using Application.AI.Common.Interfaces.Bundles;
using Domain.AI.Bundles;
using Domain.Common;
using Domain.Common.Config;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.CQRS.Bundles.RunBundle;

/// <summary>
/// Handles <see cref="RunBundleCommand"/>: refuses when bundle execution is disabled or the handle is
/// unknown/expired, creates a <see cref="BundleRunStatus.Queued"/> run record carrying the resolved
/// capability envelope, and enqueues its job id for background dispatch. Returns the job id immediately —
/// the multi-turn conversation runs out-of-band on the <c>BundleRunBackgroundService</c>.
/// </summary>
/// <remarks>
/// The agent name is captured from the staged bundle here so the dispatcher never has to re-read the bundle
/// to know what to run. Create-then-Enqueue: a host crash between the two leaves a queued record with no
/// worker to pick it up — the same non-durable loss profile as the in-memory job store and dispatch queue,
/// which is consistent with bundle runs not being persisted.
/// </remarks>
public sealed class RunBundleCommandHandler
    : IRequestHandler<RunBundleCommand, Result<RunBundleResult>>
{
    private readonly IBundleHandleStore _handleStore;
    private readonly IBundleRunJobStore _jobStore;
    private readonly IBundleRunDispatchQueue _dispatchQueue;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly TimeProvider _time;
    private readonly ILogger<RunBundleCommandHandler> _logger;

    /// <summary>Initializes a new <see cref="RunBundleCommandHandler"/>.</summary>
    public RunBundleCommandHandler(
        IBundleHandleStore handleStore,
        IBundleRunJobStore jobStore,
        IBundleRunDispatchQueue dispatchQueue,
        IOptionsMonitor<AppConfig> config,
        TimeProvider time,
        ILogger<RunBundleCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(handleStore);
        ArgumentNullException.ThrowIfNull(jobStore);
        ArgumentNullException.ThrowIfNull(dispatchQueue);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _handleStore = handleStore;
        _jobStore = jobStore;
        _dispatchQueue = dispatchQueue;
        _config = config;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<RunBundleResult>> Handle(
        RunBundleCommand request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_config.CurrentValue.AI.BundleExecution.Enabled)
        {
            return Result<RunBundleResult>.Forbidden(
                "Bundle execution is disabled. Set AppConfig.AI.BundleExecution.Enabled = true to enable it.");
        }

        var staged = _handleStore.TryGet(request.Handle);
        if (staged is null)
        {
            return Result<RunBundleResult>.NotFound(
                "Bundle handle not found or expired. Register the bundle again to obtain a fresh handle.");
        }

        var record = new BundleRunRecord
        {
            JobId = Guid.NewGuid().ToString("N"),
            Handle = request.Handle,
            AgentName = staged.Agent.Id,
            UserMessages = request.UserMessages,
            MaxTurns = request.MaxTurns,
            Envelope = request.Envelope,
            Status = BundleRunStatus.Queued,
            CreatedAt = _time.GetUtcNow()
        };

        _jobStore.Create(record);
        await _dispatchQueue.EnqueueAsync(record.JobId, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Queued bundle run {JobId} for handle {Handle} (agent {AgentId}, {MessageCount} message(s), max {MaxTurns} turns).",
            record.JobId, record.Handle, record.AgentName, record.UserMessages.Count, record.MaxTurns);

        return Result<RunBundleResult>.Success(new RunBundleResult { JobId = record.JobId });
    }
}
