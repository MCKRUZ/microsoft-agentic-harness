namespace Domain.Common.Compliance;

/// <summary>What happened to one store when a copy of a person's data was made (#902).</summary>
public enum ExportStatus
{
    /// <summary>The store returned everything it holds for the person, possibly nothing.</summary>
    Included,

    /// <summary>The store was asked and failed. The copy is incomplete.</summary>
    Failed,

    /// <summary>The store can hold the person's data but cannot return a copy of it. The copy is incomplete.</summary>
    NotExportable,

    /// <summary>
    /// The store keeps records it cannot search for one person, and nothing ages them out. The copy is incomplete.
    /// </summary>
    RetainedUnsearchable,

    /// <summary>The store keeps records it cannot search for one person until they expire on their own.</summary>
    RetainedUntilExpiry,

    /// <summary>A tamper-evident audit trail, kept on purpose and not searchable for one person.</summary>
    RetainedByObligation,

    /// <summary>Documents other people loaded, which may mention the person; the organisation's own content.</summary>
    RetainedAsBusinessContent,

    /// <summary>The store holds nothing a person would receive a copy of, such as operational counters.</summary>
    NotApplicable,
}
