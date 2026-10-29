using Domain.Common;

namespace Application.Core.Compliance;

/// <summary>
/// Reads back what the compliance audit trail recorded (#1000), so the compliance report can say which erasure, export and
/// document removal requests were handled, not only that the trail's chain is intact.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="IComplianceAuditTrail"/>, which only writes: a deployment that replaces the trail keeps its own
/// type working, and adds this when it wants its records in the report. The shipped trail implements both and is registered
/// as both; a replacement that does not implement this leaves the report with a warning that the trail cannot be read.
/// </para>
/// <para>
/// Records come back as the trail holds them: a named operator as a keyed reference, the subject as a keyed reference when
/// one is configured. A caller that shows them more widely than the trail must not show those (the report does not).
/// </para>
/// </remarks>
public interface IComplianceAuditRecords
{
    /// <summary>The records whose own timestamp falls in the period, wherever in the trail they landed.</summary>
    /// <param name="start">The period's start, inclusive.</param>
    /// <param name="end">The period's end, inclusive.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The records, and how many are still held for replay; or a failure saying why the trail could not be read.</returns>
    Task<Result<ComplianceTrailContents>> ReadAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken);
}
