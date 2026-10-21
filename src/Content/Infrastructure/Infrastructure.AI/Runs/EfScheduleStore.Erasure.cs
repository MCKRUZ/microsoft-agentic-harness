using Application.AI.Common.Interfaces.Compliance;
using Domain.Common.Compliance;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.AI.Runs;

public sealed partial class EfScheduleStore : IPersonalDataEraser
{
    /// <inheritdoc/>
    public string ErasureStoreName => "schedules";

    /// <inheritdoc/>
    public ErasureStage ErasureStage => ErasureStage.BeforeConversations;

    /// <inheritdoc/>
    /// <remarks>
    /// Matches <see cref="ErasureSubject.CanonicalOwnerId"/>: schedules are stored canonicalized. Every tenant, because
    /// erasure is about the person, not the tenant the schedule was created under.
    /// </remarks>
    public async Task<int> EraseSubjectAsync(ErasureSubject subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);

        await using var ctx = await _factory.CreateDbContextAsync(cancellationToken);
        return await ctx.Schedules
            .Where(e => e.OwnerId == subject.CanonicalOwnerId)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
