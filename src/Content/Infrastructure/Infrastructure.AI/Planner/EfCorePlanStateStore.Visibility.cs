using Infrastructure.AI.Persistence;
using Infrastructure.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.AI.Planner;

public sealed partial class EfCorePlanStateStore
{
    /// <summary>
    /// Returns the plan graphs the ambient caller may READ, delegating to
    /// <see cref="PlannerScopeFilter.VisibleTo"/> (shared with the attestation store):
    /// tenant matches or is null/global, AND owner matches or is null/unowned.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT a system bypass: a caller with no ambient identity sees only global
    /// plans — closed-by-default for the future HTTP host, where an anonymous request must
    /// never enumerate tenant- or owner-stamped plans. Request-initiated executor work is
    /// unaffected — the ambient scope flows via <c>AsyncLocal</c> into child DI scopes and
    /// post-turn continuations (see <c>KnowledgeScopeAccessor</c>).
    /// </remarks>
    /// <param name="ctx">The active planner context.</param>
    /// <returns>A query over the plans the ambient caller may see.</returns>
    private IQueryable<PlanGraphEntity> VisiblePlans(PlannerDbContext ctx)
        => PlannerScopeFilter.VisibleTo(ctx.PlanGraphs, _scope);

    /// <summary>
    /// Returns the plan graphs the ambient caller may MUTATE, delegating to
    /// <see cref="PlannerScopeFilter.WritableBy"/>: exact tenant AND owner equality.
    /// A globally readable (null-owner) plan is never mutable by a scoped caller.
    /// </summary>
    /// <param name="ctx">The active planner context.</param>
    /// <returns>A query over the plans the ambient caller may mutate.</returns>
    private IQueryable<PlanGraphEntity> WritablePlans(PlannerDbContext ctx)
        => PlannerScopeFilter.WritableBy(ctx.PlanGraphs, _scope);

    /// <summary>
    /// Determines whether the plan exists AND is visible to the ambient caller. Callers must
    /// map <c>false</c> to the same NotFound/null/empty result they return for a missing plan,
    /// so cross-owner access is indistinguishable from absence (404-not-403).
    /// </summary>
    /// <param name="ctx">The active planner context.</param>
    /// <param name="planId">The plan identifier to probe.</param>
    /// <param name="ct">Cancellation token.</param>
    private Task<bool> IsPlanVisibleAsync(PlannerDbContext ctx, Guid planId, CancellationToken ct)
        => VisiblePlans(ctx).AnyAsync(g => g.Id == planId, ct);

    /// <summary>
    /// Determines whether the plan exists AND is mutable by the ambient caller. Same
    /// NotFound-on-failure contract as <see cref="IsPlanVisibleAsync"/> — a plan the caller
    /// can read but not mutate still reads as NotFound on the write path, never Forbidden.
    /// </summary>
    /// <param name="ctx">The active planner context.</param>
    /// <param name="planId">The plan identifier to probe.</param>
    /// <param name="ct">Cancellation token.</param>
    private Task<bool> IsPlanWritableAsync(PlannerDbContext ctx, Guid planId, CancellationToken ct)
        => WritablePlans(ctx).AnyAsync(g => g.Id == planId, ct);

    /// <summary>
    /// Determines whether the step exists on a plan mutable by the ambient caller. Same
    /// NotFound-on-failure contract as <see cref="IsPlanWritableAsync"/>.
    /// </summary>
    /// <param name="ctx">The active planner context.</param>
    /// <param name="stepId">The step identifier to probe.</param>
    /// <param name="ct">Cancellation token.</param>
    private Task<bool> IsStepWritableAsync(PlannerDbContext ctx, Guid stepId, CancellationToken ct)
        => WritablePlans(ctx).AnyAsync(g => g.Steps.Any(s => s.Id == stepId), ct);
}
