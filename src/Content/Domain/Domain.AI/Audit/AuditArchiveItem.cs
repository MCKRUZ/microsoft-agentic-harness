namespace Domain.AI.Audit;

/// <summary>A file in the audit archive (#416).</summary>
/// <param name="Name">Its name, without a folder.</param>
/// <param name="Length">Its size in bytes.</param>
/// <param name="LastModified">When it was written.</param>
public sealed record AuditArchiveItem(string Name, long Length, DateTimeOffset LastModified);
