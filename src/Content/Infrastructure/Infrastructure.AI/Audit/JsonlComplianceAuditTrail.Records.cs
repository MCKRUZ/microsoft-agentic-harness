using System.Text.Json;
using Application.Core.Compliance;
using Domain.Common;
using Domain.Common.Models;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Audit;

/// <summary>
/// Reading the trail back for the compliance report (#1000): <see cref="IComplianceAuditRecords"/>.
/// </summary>
/// <remarks>
/// A record is in the period by its own <c>timestamp</c>, not by the day-file it is in: a replayed record lands in a later
/// day-file than the one it was made on. So every day-file from the day before the period starts onward is read (one day of
/// margin for a host whose clock ran behind), archived ones included. A line that is not a record of this trail is skipped
/// and counted in a warning, never returned.
/// </remarks>
public sealed partial class JsonlComplianceAuditTrail
{
    /// <inheritdoc />
    public async Task<Result<ComplianceTrailContents>> ReadAsync(
        DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        var from = DatedAuditSegments.WithClockMargin(DateOnly.FromDateTime(start.UtcDateTime));
        var records = new List<AuditEntry>();
        var unreadable = 0;
        try
        {
            await foreach (var payload in _chain.ReadPayloadsAsync(path => DatedAuditSegments.MayHoldRecordsFrom(path, from), cancellationToken)
                               .ConfigureAwait(false))
            {
                if (Parse(payload) is not { } record)
                    unreadable++;
                else if (record.Timestamp >= start && record.Timestamp <= end)
                    records.Add(record);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to read the compliance audit trail at {AuditLocation}", _folder);
            // A fixed reason: the report travels further than the host log, and an I/O message names the trail's path.
            return Result<ComplianceTrailContents>.Fail("The compliance audit trail could not be read; the host log says why.");
        }

        if (unreadable > 0)
            _logger.LogWarning("Skipped {Count} line(s) in {AuditLocation} that are not compliance records.", unreadable, _folder);

        return Result<ComplianceTrailContents>.Success(new ComplianceTrailContents(records, _chain.HeldRecordCount));
    }

    private static AuditEntry? Parse(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<AuditEntry>(payload, SerializeOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
