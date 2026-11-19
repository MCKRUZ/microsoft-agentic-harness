using Domain.Common.Models;

namespace Application.Core.Compliance;

/// <summary>What <see cref="IComplianceAuditRecords.ReadAsync"/> read from the compliance audit trail for one period.</summary>
/// <param name="Records">The records whose timestamp falls in the period, in the order the trail holds them.</param>
/// <param name="HeldForReplay">Records held for replay (#995), whatever their period: not in the trail yet, so not read.</param>
public sealed record ComplianceTrailContents(IReadOnlyList<AuditEntry> Records, int HeldForReplay);
