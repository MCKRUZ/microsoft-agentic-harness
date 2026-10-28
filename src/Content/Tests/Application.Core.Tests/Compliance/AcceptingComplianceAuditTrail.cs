using Application.Core.Compliance;
using Domain.Common;
using Domain.Common.Models;

namespace Application.Core.Tests.Compliance;

/// <summary>
/// A compliance audit trail that accepts every record, for tests about something else. A loose mock would return a null
/// result, so every record would quietly take the failure path.
/// </summary>
internal sealed class AcceptingComplianceAuditTrail : IComplianceAuditTrail
{
    /// <inheritdoc />
    public Task<Result> RecordAsync(AuditEntry entry, ErasureActor actor, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());
}
