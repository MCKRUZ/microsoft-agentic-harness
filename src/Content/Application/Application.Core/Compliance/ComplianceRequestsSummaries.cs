using Domain.AI.Compliance;
using Domain.Common.Models;

namespace Application.Core.Compliance;

/// <summary>
/// Summarises the compliance audit trail's records for the compliance report (#1000): counts by request and outcome, and
/// each finished request's receipt id. Reads only the action, outcome, timestamp, receipt id and completeness of a record;
/// the executor, subject reference and document id never reach the summary.
/// </summary>
public static class ComplianceRequestsSummaries
{
    private const string ReceiptIdKey = "receipt_request_id";
    private const string ExportIdKey = "export_request_id";
    private const string CompletenessKey = "completeness";
    private const string CompleteKey = "complete";

    /// <summary>Builds the summary of <paramref name="contents"/>.</summary>
    /// <param name="contents">What the trail recorded in the period.</param>
    /// <param name="maxReceipts">The most receipts to list; the most recent are kept.</param>
    /// <returns>The summary.</returns>
    public static ComplianceRequestsSummary From(ComplianceTrailContents contents, int maxReceipts)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentOutOfRangeException.ThrowIfNegative(maxReceipts);

        var counts = contents.Records
            .GroupBy(r => (r.Action, Outcome: r.Outcome.ToString()))
            .OrderBy(g => g.Key.Action, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Outcome, StringComparer.Ordinal)
            .Select(g => new ComplianceRequestCount { Action = g.Key.Action, Outcome = g.Key.Outcome, Count = g.Count() })
            .ToList();

        // By timestamp, not chain position: a replayed record lands after records made later.
        var receipts = contents.Records
            .Select(ReceiptOf)
            .OfType<ComplianceRequestReceipt>()
            .OrderBy(r => r.RecordedAt)
            .ToList();

        return new ComplianceRequestsSummary
        {
            TotalRecords = contents.Records.Count,
            Counts = counts,
            Receipts = receipts.Count <= maxReceipts ? receipts : receipts.Skip(receipts.Count - maxReceipts).ToList(),
            TotalReceipts = receipts.Count,
            HeldForReplay = contents.HeldForReplay,
        };
    }

    private static ComplianceRequestReceipt? ReceiptOf(AuditEntry record)
    {
        var metadata = record.Metadata;
        if (metadata is null)
            return null;

        if (metadata.TryGetValue(ReceiptIdKey, out var receiptId))
            return Receipt(record, receiptId, metadata.GetValueOrDefault(CompletenessKey) ?? "Unknown");

        if (metadata.TryGetValue(ExportIdKey, out var exportId))
            return Receipt(record, exportId, metadata.GetValueOrDefault(CompleteKey) == "true" ? "Complete" : "Incomplete");

        return null;
    }

    private static ComplianceRequestReceipt Receipt(AuditEntry record, string receiptId, string completeness) => new()
    {
        Action = record.Action,
        ReceiptId = receiptId,
        Completeness = completeness,
        RecordedAt = record.Timestamp,
    };
}
