namespace Domain.AI.Audit;

/// <summary>The outcome of writing a file into the write-once audit archive (#416).</summary>
public enum AuditArchivePut
{
    /// <summary>The file was written.</summary>
    Created,

    /// <summary>A file was already there with exactly these bytes: nothing to do.</summary>
    Identical,

    /// <summary>A file was already there with other bytes. Write-once storage cannot replace it.</summary>
    Different,
}
