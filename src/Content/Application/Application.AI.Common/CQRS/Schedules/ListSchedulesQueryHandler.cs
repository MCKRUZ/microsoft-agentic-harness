using Application.AI.Common.Interfaces.Runs;
using Domain.Common;
using MediatR;

namespace Application.AI.Common.CQRS.Schedules;

/// <summary>Handles <see cref="ListSchedulesQuery"/>.</summary>
public sealed class ListSchedulesQueryHandler : IRequestHandler<ListSchedulesQuery, Result<IReadOnlyList<ScheduleSummary>>>
{
    private readonly IScheduleStore _store;

    public ListSchedulesQueryHandler(IScheduleStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<ScheduleSummary>>> Handle(
        ListSchedulesQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var records = await _store.ListForOwnerAsync(request.OwnerId, request.TenantId, cancellationToken);
        IReadOnlyList<ScheduleSummary> summaries = records.Select(ScheduleSummary.FromRecord).ToList();
        return Result<IReadOnlyList<ScheduleSummary>>.Success(summaries);
    }
}
