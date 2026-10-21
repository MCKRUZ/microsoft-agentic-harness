using Application.AI.Common.Interfaces.Compliance;
using Domain.Common.Compliance;
using Infrastructure.AI.Extensions;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.AI.Planner;

public sealed partial class EfCorePlanStateStore : IPersonalDataEraser, IPersonalDataScrubber
{
    /// <inheritdoc/>
    public string ErasureStoreName => "plans";

    /// <inheritdoc/>
    public ErasureStage ErasureStage => ErasureStage.BeforeConversations;

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Matches <see cref="ErasureSubject.CanonicalOwnerId"/>: plans are stamped through
    /// <c>ScopeIdentity.Canonicalize</c>. Returns the number of plans removed.
    /// </para>
    /// <para>
    /// <strong>Children are deleted explicitly, in dependency order, in one transaction.</strong> A plan's edges point at
    /// its steps with a restrictive foreign key, and the database does not promise which of a plan's cascades runs first,
    /// so a plain plan delete can fail on the edge-to-step constraint. Deleting edges, step states, logs and steps before
    /// the plans themselves removes that ordering question. A sub-plan owned by someone else keeps its row; its link to
    /// an erased parent is cleared by the schema's <c>SET NULL</c>.
    /// </para>
    /// </remarks>
    public async Task<int> EraseSubjectAsync(ErasureSubject subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);

        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        var planIds = await context.PlanGraphs
            .Where(p => p.OwnerId == subject.CanonicalOwnerId)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);
        if (planIds.Count == 0)
            return 0;

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.PlanEdges.Where(e => planIds.Contains(e.PlanGraphId)).ExecuteDeleteAsync(cancellationToken);
        await context.StepExecutionStates
            .Where(s => context.PlanSteps.Any(step => step.Id == s.StepId && planIds.Contains(step.PlanGraphId)))
            .ExecuteDeleteAsync(cancellationToken);
        await context.PlanExecutionLogs.Where(l => planIds.Contains(l.PlanGraphId)).ExecuteDeleteAsync(cancellationToken);
        await context.PlanSteps.Where(s => planIds.Contains(s.PlanGraphId)).ExecuteDeleteAsync(cancellationToken);
        var removed = await context.PlanGraphs.Where(p => planIds.Contains(p.Id)).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return removed;
    }

    /// <inheritdoc/>
    public async Task ScrubAsync(CancellationToken cancellationToken)
    {
        await using var context = await _factory.CreateDbContextAsync(cancellationToken);
        await context.Database.TruncateWriteAheadLogAsync(cancellationToken);
    }
}
