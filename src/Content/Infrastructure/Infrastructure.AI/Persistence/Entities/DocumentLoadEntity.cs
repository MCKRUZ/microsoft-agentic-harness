namespace Infrastructure.AI.Persistence.Entities;

/// <summary>One document and when it was last loaded (#901). Nothing else: not who loaded it, nor from where.</summary>
public sealed class DocumentLoadEntity
{
    /// <summary>The document id, as the chunkers form it.</summary>
    public required string DocumentId { get; set; }

    /// <summary>When the document was last loaded.</summary>
    public DateTimeOffset LastLoadedAt { get; set; }
}
