using Application.AI.Common.Interfaces.Runs;
using Domain.Common;
using MediatR;

namespace Application.AI.Common.CQRS.Schedules;

/// <summary>Handles <see cref="DeleteScheduleCommand"/>.</summary>
public sealed class DeleteScheduleCommandHandler : IRequestHandler<DeleteScheduleCommand, Result>
{
    private readonly IScheduleStore _store;

    public DeleteScheduleCommandHandler(IScheduleStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public async Task<Result> Handle(DeleteScheduleCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var deleted = await _store.DeleteAsync(request.ScheduleId, request.OwnerId, request.TenantId, cancellationToken);
        return deleted
            ? Result.Success()
            : Result.NotFound($"No schedule {request.ScheduleId} found.");
    }
}
