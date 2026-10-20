namespace Infrastructure.AI.Audit;

/// <summary>The outcome of an <see cref="AuditArchiver"/> step (#416).</summary>
public enum AuditArchiveStatus
{
    /// <summary>The step completed.</summary>
    Done,

    /// <summary>The step stopped at something that must be investigated: a gap, an edit, a copy that differs.</summary>
    Stopped,

    /// <summary>The archive is not mounted or was never set up.</summary>
    NotReady,
}
