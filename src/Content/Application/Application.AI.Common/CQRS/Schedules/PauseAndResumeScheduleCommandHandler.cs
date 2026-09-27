using Application.AI.Common.Interfaces.Runs;
using Domain.Common;
using MediatR;

namespace Application.AI.Common.CQRS.Schedules;

/// <summary>
/// Handles both <see cref="PauseScheduleCommand"/> and <see cref="ResumeScheduleCommand"/> — the two
/// operations are identical (a version-guarded flip of <c>ScheduleRecord.Enabled</c>) at every layer
/// below the command names themselves; <see cref="IScheduleStore"/>'s own
/// <c>PauseAsync</c>/<c>ResumeAsync</c> pair and <c>ManageSchedulesTool</c>'s
/// <c>PauseOrResumeAsync</c> already share one code path for the same reason. Kept as one handler
/// class rather than two near-duplicate files.
/// </summary>
public sealed class PauseAndResumeScheduleCommandHandler :
    IRequestHandler<PauseScheduleCommand, Result>,
    IRequestHandler<ResumeScheduleCommand, Result>
{
    private readonly IScheduleStore _store;

    public PauseAndResumeScheduleCommandHandler(IScheduleStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public Task<Result> Handle(PauseScheduleCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SetEnabledAsync(
            request.ScheduleId, request.OwnerId, request.TenantId, request.ExpectedVersion,
            _store.PauseAsync, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result> Handle(ResumeScheduleCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SetEnabledAsync(
            request.ScheduleId, request.OwnerId, request.TenantId, request.ExpectedVersion,
            _store.ResumeAsync, cancellationToken);
    }

    /// <remarks>
    /// <strong>Known TOCTOU in the failure classification below.</strong> <c>existing.Version</c> comes
    /// from a separate, earlier round-trip than the write <paramref name="apply"/> performs, so the
    /// Conflict-vs-NotFound split is a best-effort reconstruction, not the store's actual state at
    /// write time — a schedule modified again between the two reads can report the wrong one of the
    /// two in either direction. Low severity (no data loss, only the returned error code is affected)
    /// and not fixed here: a correct fix needs the store's write itself to report why it failed, not
    /// just whether, which is a real interface change rather than a local one.
    /// </remarks>
    private async Task<Result> SetEnabledAsync(
        string scheduleId, string ownerId, string? tenantId, int expectedVersion,
        Func<string, string, string?, int, CancellationToken, Task<bool>> apply,
        CancellationToken cancellationToken)
    {
        var existing = await _store.GetAsync(scheduleId, ownerId, tenantId, cancellationToken);
        if (existing is null)
            return Result.NotFound($"No schedule {scheduleId} found.");

        var applied = await apply(scheduleId, ownerId, tenantId, expectedVersion, cancellationToken);
        if (applied)
            return Result.Success();

        return existing.Version != expectedVersion
            ? Result.Conflict("The schedule was modified since it was last read. Re-read it and try again.")
            : Result.NotFound($"No schedule {scheduleId} found.");
    }
}
