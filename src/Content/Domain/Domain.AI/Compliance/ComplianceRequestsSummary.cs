namespace Domain.AI.Compliance;

/// <summary>
/// What the compliance audit trail recorded in a report's period (#1000): how many erasure, data export and document
/// removal records there were, by request and outcome, and the receipt each finished request was given.
/// </summary>
/// <remarks>
/// Only what an operator needs to see which requests were handled. No executor, subject reference or document id is
/// included: the trail keeps those (an operator and a subject as keyed references) for whoever holds the key, and a report
/// is shared more widely than the trail. A receipt id is a random id made for the request, tied to no one.
/// </remarks>
public sealed record ComplianceRequestsSummary
{
    /// <summary>A summary of a trail that recorded nothing in the period.</summary>
    public static ComplianceRequestsSummary Empty { get; } = new()
    {
        TotalRecords = 0,
        Counts = [],
        Receipts = [],
        TotalReceipts = 0,
        HeldForReplay = 0,
    };

    /// <summary>Records in the trail whose own timestamp falls in the period, attempts and outcomes alike.</summary>
    public required int TotalRecords { get; init; }

    /// <summary>The records counted by request and outcome, ordered by request then outcome.</summary>
    public required IReadOnlyList<ComplianceRequestCount> Counts { get; init; }

    /// <summary>
    /// The receipts given in the period, oldest first: the most recent ones when there were more than the report's
    /// per-source limit (<see cref="TotalReceipts"/> says how many there were).
    /// </summary>
    public required IReadOnlyList<ComplianceRequestReceipt> Receipts { get; init; }

    /// <summary>Every receipt recorded in the period, before the per-source limit.</summary>
    public required int TotalReceipts { get; init; }

    /// <summary>
    /// Records the trail could not append and holds for replay (#995), across every period: not counted above until replay
    /// writes them in, usually within a minute.
    /// </summary>
    public required int HeldForReplay { get; init; }
}

/// <summary>How many compliance records one request kind left with one outcome.</summary>
public sealed record ComplianceRequestCount
{
    /// <summary>The request: <c>right_to_erasure</c>, <c>subject_access_export</c> or <c>document_removal</c>.</summary>
    public required string Action { get; init; }

    /// <summary>The record's outcome: <c>Attempted</c> for a request begun, then <c>Success</c>, <c>Failure</c> or <c>Denied</c>.</summary>
    public required string Outcome { get; init; }

    /// <summary>How many records had this request and outcome.</summary>
    public required int Count { get; init; }
}

/// <summary>The receipt one finished compliance request was given, as its outcome record names it.</summary>
public sealed record ComplianceRequestReceipt
{
    /// <summary>The request the receipt was given for.</summary>
    public required string Action { get; init; }

    /// <summary>The receipt's request id, as the person or operator was handed it.</summary>
    public required string ReceiptId { get; init; }

    /// <summary>How complete the request was: <c>Full</c> or <c>Partial</c>, or for an export <c>Complete</c> or <c>Incomplete</c>.</summary>
    public required string Completeness { get; init; }

    /// <summary>When the outcome was recorded.</summary>
    public required DateTimeOffset RecordedAt { get; init; }
}
