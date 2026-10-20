namespace Infrastructure.AI.Audit;

/// <summary>What one <see cref="AuditArchiver"/> step did for one audit log (#416).</summary>
/// <param name="Chain">The log's name.</param>
/// <param name="Status">The outcome.</param>
/// <param name="FilesArchived">How many files were archived (and deleted from the live folder) this run.</param>
/// <param name="Problem">Why the step stopped, when it did not succeed.</param>
public sealed record AuditArchiveResult(string Chain, AuditArchiveStatus Status, int FilesArchived, string? Problem)
{
    /// <summary>A step that succeeded.</summary>
    /// <param name="chain">The log's name.</param>
    /// <param name="filesArchived">Files archived this run.</param>
    /// <returns>The result.</returns>
    public static AuditArchiveResult Done(string chain, int filesArchived = 0) =>
        new(chain, AuditArchiveStatus.Done, filesArchived, null);

    /// <summary>A step that found something someone must look at; nothing after it was archived.</summary>
    /// <param name="chain">The log's name.</param>
    /// <param name="filesArchived">Files archived this run before the problem was found.</param>
    /// <param name="problem">What was found.</param>
    /// <returns>The result.</returns>
    public static AuditArchiveResult Stopped(string chain, int filesArchived, string problem) =>
        new(chain, AuditArchiveStatus.Stopped, filesArchived, problem);

    /// <summary>The archive is not ready: its marker file is missing.</summary>
    /// <param name="chain">The log's name.</param>
    /// <param name="problem">What is missing.</param>
    /// <returns>The result.</returns>
    public static AuditArchiveResult NotReady(string chain, string problem) =>
        new(chain, AuditArchiveStatus.NotReady, 0, problem);
}
