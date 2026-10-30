using Application.Core.Compliance;
using Domain.Common;

namespace Infrastructure.AI.Audit;

/// <summary>
/// The compliance records reader a host gets when it replaced <see cref="IComplianceAuditTrail"/> with a trail that cannot be
/// read back (#1000): every read fails with a fixed reason, so the compliance report carries a warning instead of failing.
/// </summary>
internal sealed class UnreadableComplianceAuditRecords : IComplianceAuditRecords
{
    /// <inheritdoc />
    public Task<Result<ComplianceTrailContents>> ReadAsync(
        DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken) =>
        Task.FromResult(Result<ComplianceTrailContents>.Fail(
            "The registered compliance audit trail does not implement IComplianceAuditRecords, so its records cannot be read."));
}
