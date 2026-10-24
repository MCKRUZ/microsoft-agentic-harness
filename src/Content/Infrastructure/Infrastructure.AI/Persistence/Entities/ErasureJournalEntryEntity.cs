namespace Infrastructure.AI.Persistence.Entities;

/// <summary>
/// One id an erasure request read for one person and had not finished with (#907). Keyed by owner, kind and id, so a
/// later request that reads the same id takes the row over instead of adding a second one.
/// </summary>
public sealed class ErasureJournalEntryEntity
{
    /// <summary>The person, as their identity provider issued the id.</summary>
    public required string OwnerId { get; set; }

    /// <summary>What the id is: one of the <c>ErasureJournalKind</c> names.</summary>
    public required string Kind { get; set; }

    /// <summary>The conversation, step, tool-result scope or budget id.</summary>
    public required string Value { get; set; }

    /// <summary>The erasure request that last recorded this row; only it may clear the row.</summary>
    public required string RequestId { get; set; }

    /// <summary>When the row was last recorded; the journal retention counts from here.</summary>
    public DateTimeOffset RecordedAt { get; set; }
}
