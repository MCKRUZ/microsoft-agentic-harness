using Domain.Common.Compliance;

namespace Application.AI.Common.Interfaces.Compliance;

/// <summary>
/// Lists the ids a person's planned-task runs recorded data under, for right-to-erasure (#906).
/// </summary>
/// <remarks>
/// <para>
/// Implemented by the plan store, which records each run against the person who ran it. The erasure coordinator reads
/// it once, before anything is deleted, beside the person's conversation ids; the stores keyed by conversation id then
/// reach the runs' step sessions too. The plan store itself is erased with the conversations, after those stores, so a
/// failed request can be retried and still find them.
/// </para>
/// <para>
/// A host whose plan store does not implement this collects nothing from it; such a store also cannot erase, so the
/// receipt is already <c>Partial</c> and names it.
/// </para>
/// </remarks>
public interface IPlanRunTraceSource
{
    /// <summary>The ids every planned-task run <paramref name="ownerId"/> ran recorded data under.</summary>
    /// <param name="ownerId">The person, in either form; matched the way plans are stamped.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The run ids found, or <see cref="PlanRunTraces.Empty"/>.</returns>
    Task<PlanRunTraces> ListRunTracesAsync(string ownerId, CancellationToken cancellationToken);
}
