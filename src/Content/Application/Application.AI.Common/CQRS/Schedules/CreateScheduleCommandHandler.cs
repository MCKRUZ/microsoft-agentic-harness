using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Interfaces.Runs;
using Application.AI.Common.Services.Runs;
using Domain.AI.Planner;
using Domain.AI.Runs;
using Domain.Common;
using Domain.Common.Config;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.CQRS.Schedules;

/// <summary>
/// Handles <see cref="CreateScheduleCommand"/>: confirms the caller owns the target (for
/// <see cref="RunKind.Workflow"/>), bounds how many schedules one caller may hold, computes the
/// initial fire time, and stores the schedule.
/// </summary>
public sealed class CreateScheduleCommandHandler : IRequestHandler<CreateScheduleCommand, Result<ScheduleSummary>>
{
    private readonly IScheduleStore _scheduleStore;
    private readonly IPlanStateStore _planStore;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly TimeProvider _time;
    private readonly ILogger<CreateScheduleCommandHandler> _logger;

    public CreateScheduleCommandHandler(
        IScheduleStore scheduleStore,
        IPlanStateStore planStore,
        IOptionsMonitor<AppConfig> config,
        TimeProvider time,
        ILogger<CreateScheduleCommandHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(scheduleStore);
        ArgumentNullException.ThrowIfNull(planStore);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _scheduleStore = scheduleStore;
        _planStore = planStore;
        _config = config;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result<ScheduleSummary>> Handle(CreateScheduleCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var config = _config.CurrentValue.AI.Schedules;
        if (!config.Enabled)
        {
            return Result<ScheduleSummary>.Forbidden(
                "Recurring schedules are disabled. Set AppConfig.AI.Schedules.Enabled = true to enable them.");
        }

        if (request.Kind == RunKind.Workflow)
        {
            var ownershipCheck = await VerifyWorkflowOwnershipAsync(request.TargetId, cancellationToken);
            if (ownershipCheck is not null)
                return ownershipCheck;
        }

        var quotaCheck = await EnforceQuotaAsync(request, config.MaxSchedulesPerOwner, cancellationToken);
        if (quotaCheck is not null)
            return quotaCheck;

        DateTimeOffset nextFireAt;
        try
        {
            nextFireAt = ScheduleOccurrenceCalculator.ComputeNextOccurrence(
                request.CronExpression, request.TimeZoneId, _time.GetUtcNow(),
                request.ActiveHoursStart, request.ActiveHoursEnd);
        }
        catch (Exception ex)
        {
            // The validator already confirmed the expression parses and the time zone resolves;
            // reaching here means the active-hours window cannot be satisfied at all — a real
            // rejection, not an internal fault.
            _logger.LogWarning(ex, "Could not compute an initial fire time for a new schedule.");
            return Result<ScheduleSummary>.ValidationFailure(
                ["Could not find any occurrence of this cron expression within the configured active-hours window."]);
        }

        var record = BuildRecord(request, nextFireAt);
        await _scheduleStore.CreateAsync(record, cancellationToken);

        _logger.LogInformation(
            "Created schedule {ScheduleId} for {Kind} {TargetId}, next fire at {NextFireAt}.",
            record.ScheduleId, record.Kind, record.TargetId, record.NextFireAt);

        return Result<ScheduleSummary>.Success(ScheduleSummary.FromRecord(record));
    }

    /// <summary>
    /// Enforces the per-owner schedule quota. Soft cap: read-then-write, not atomic against a
    /// concurrent create from the same caller. A caller briefly exceeding this by one or two under a
    /// race is a quota nuisance, not a security boundary — unlike run admission, nothing downstream
    /// treats this count as a hard resource limit, so the extra complexity of an atomic admission path
    /// is not warranted here.
    /// </summary>
    /// <returns>A failure result to return immediately, or <see langword="null"/> when under quota.</returns>
    private async Task<Result<ScheduleSummary>?> EnforceQuotaAsync(
        CreateScheduleCommand request, int maxSchedulesPerOwner, CancellationToken cancellationToken)
    {
        var existingCount = await _scheduleStore.CountForOwnerAsync(request.OwnerId, request.TenantId, cancellationToken);
        if (existingCount < maxSchedulesPerOwner)
            return null;

        return Result<ScheduleSummary>.ValidationFailure(
            [$"This caller already has {maxSchedulesPerOwner} schedule(s), the maximum this "
             + "host permits. Delete one before creating another."]);
    }

    private ScheduleRecord BuildRecord(CreateScheduleCommand request, DateTimeOffset nextFireAt) => new()
    {
        ScheduleId = Guid.NewGuid().ToString("N"),
        Kind = request.Kind,
        TargetId = request.TargetId,
        OwnerId = request.OwnerId,
        TenantId = request.TenantId,
        Envelope = request.Envelope,
        CronExpression = request.CronExpression,
        TimeZoneId = request.TimeZoneId,
        ActiveHoursStart = request.ActiveHoursStart,
        ActiveHoursEnd = request.ActiveHoursEnd,
        Cooldown = request.Cooldown,
        MissedRunPolicy = request.MissedRunPolicy,
        Enabled = true,
        NextFireAt = nextFireAt,
        CreatedAt = _time.GetUtcNow(),
        Version = 0,
    };

    /// <summary>
    /// Confirms the caller may write the workflow this schedule would target, mirroring
    /// <c>StartWorkflowRunCommandHandler</c>'s own check — a schedule that outlives the caller's
    /// access to the workflow it targets would otherwise keep firing against a plan the caller could
    /// no longer even see.
    /// </summary>
    /// <returns>A failure result to return immediately, or <see langword="null"/> when ownership holds.</returns>
    private async Task<Result<ScheduleSummary>?> VerifyWorkflowOwnershipAsync(string targetId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(targetId, out var workflowGuid))
            return Result<ScheduleSummary>.ValidationFailure(["TargetId must be a workflow id for Kind = Workflow."]);

        var owned = await _planStore.IsPlanWritableByCallerAsync(new PlanId(workflowGuid), cancellationToken);
        if (!owned.IsSuccess)
        {
            _logger.LogError(
                "Could not resolve workflow {WorkflowId} while creating a schedule: {Errors}",
                workflowGuid, string.Join("; ", owned.Errors));
            return Result<ScheduleSummary>.Fail("The workflow could not be read.");
        }

        return owned.Value ? null : Result<ScheduleSummary>.NotFound($"No workflow {workflowGuid} found.");
    }
}
