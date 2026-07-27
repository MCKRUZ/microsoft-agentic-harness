using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Scoping;
using Infrastructure.AI.Persistence.Entities;

namespace Infrastructure.AI.Persistence;

/// <summary>
/// Shared scope predicates for every consumer of <see cref="PlannerDbContext"/>
/// (<c>EfCorePlanStateStore</c>, <c>EfCoreAttestationStore</c>), so plan visibility and
/// writability are decided identically no matter which store a future host arms.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reads</b> use <see cref="VisibleTo"/> — the knowledge-record sharing semantics
/// (tenant matches or record tenant is null/global, AND owner matches or record owner is
/// null/unowned), mirroring <c>TenantIsolatedGraphStore</c>.
/// </para>
/// <para>
/// <b>Writes</b> use <see cref="WritableBy"/> — strict exact-match: the record's
/// canonicalized tenant AND owner must equal the caller's. A null-owner (global) plan is
/// therefore readable by everyone but mutable only by a caller with no ambient identity
/// (system context); a scoped caller can never mutate a plan they don't own.
/// </para>
/// <para>
/// Caller identity is canonicalized via <see cref="ScopeIdentity.Canonicalize"/> — the same
/// form stamped on write — so plain SQL equality is exact, and EF Core rewrites the
/// null-parameter comparisons to <c>IS NULL</c>.
/// </para>
/// </remarks>
internal static class PlannerScopeFilter
{
    /// <summary>
    /// Maximum persisted length for a canonicalized owner or tenant identity. Mirrors the
    /// <c>HasMaxLength</c> mapping on <see cref="PlanGraphEntity"/>; writers must guard
    /// against longer values explicitly because SQLite does not enforce column lengths.
    /// </summary>
    internal const int MaxIdentityLength = 256;

    /// <summary>
    /// Filters to the plans the ambient caller may read: tenant matches (or the plan's
    /// tenant is null/global) AND owner matches (or the plan's owner is null/unowned).
    /// A caller without identity sees only global plans — closed-by-default.
    /// </summary>
    /// <param name="plans">The plan graph query to filter.</param>
    /// <param name="scope">The ambient knowledge scope of the caller.</param>
    /// <returns>The scope-filtered query.</returns>
    internal static IQueryable<PlanGraphEntity> VisibleTo(
        IQueryable<PlanGraphEntity> plans, IKnowledgeScope scope)
    {
        var owner = ScopeIdentity.Canonicalize(scope.UserId);
        var tenant = ScopeIdentity.Canonicalize(scope.TenantId);

        return plans.Where(g =>
            (g.TenantId == null || g.TenantId == tenant) &&
            (g.OwnerId == null || g.OwnerId == owner));
    }

    /// <summary>
    /// Filters to the plans the ambient caller may mutate: the plan's canonicalized tenant
    /// AND owner must exactly equal the caller's. Unlike <see cref="VisibleTo"/>, a
    /// null-owner (global) plan is writable only when the caller's canonicalized owner is
    /// also null — shared visibility never grants shared mutation.
    /// </summary>
    /// <param name="plans">The plan graph query to filter.</param>
    /// <param name="scope">The ambient knowledge scope of the caller.</param>
    /// <returns>The scope-filtered query.</returns>
    internal static IQueryable<PlanGraphEntity> WritableBy(
        IQueryable<PlanGraphEntity> plans, IKnowledgeScope scope)
    {
        var owner = ScopeIdentity.Canonicalize(scope.UserId);
        var tenant = ScopeIdentity.Canonicalize(scope.TenantId);

        return plans.Where(g => g.TenantId == tenant && g.OwnerId == owner);
    }
}
