using Application.AI.Common.Interfaces.Compliance;
using Domain.Common.Compliance;

namespace Infrastructure.AI.Context;

public sealed partial class FileSystemToolResultStore : IPersonalDataEraser
{
    /// <inheritdoc />
    public string ErasureStoreName => "spilled tool results";

    /// <inheritdoc />
    public ErasureStage ErasureStage => ErasureStage.BeforeConversations;

    /// <inheritdoc />
    public ErasureReach ErasureReach => ErasureReach.NamedConversations;

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Spills live under <c>{StoragePath}/{scopeId}/tool-results/</c>, where the scope is a conversation, a planned-task
    /// model step, or a planned-task run's own scope for its tool steps (#906). Each id goes through the same segment
    /// check a write does before it becomes a path, so an id can never reach outside the storage root; one the check
    /// refuses could never have been written to, and is skipped. Each removed file is also evicted from the page cache,
    /// or a copy would keep being served for up to its sliding expiry.
    /// </para>
    /// <para>Returns the number of spill files removed.</para>
    /// </remarks>
    public Task<int> EraseSubjectAsync(ErasureSubject subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);

        var removed = 0;
        foreach (var (_, scopeDirectory, directory) in SubjectSpillDirectories(subject.AllConversationIds, subject.PlanRuns))
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                File.Delete(file);
                _pageCache.Remove(file);
                removed++;
            }

            Directory.Delete(directory);
            TryDeleteIfEmpty(scopeDirectory);
        }

        return Task.FromResult(removed);
    }

    /// <summary>
    /// The spill directories that exist for one person: one per conversation id and per planned-task run scope. Erasure and
    /// export both read through this, so a copy holds exactly what an erasure deletes. Each id goes through the same
    /// segment check a write does; one the check refuses could never have been written to, and is skipped.
    /// </summary>
    /// <param name="allConversationIds">The person's conversations and their planned tasks' model steps.</param>
    /// <param name="planRuns">The person's planned-task runs, whose tool steps spill under the run's own scope.</param>
    /// <returns>
    /// Each existing scope directory with its <c>tool-results</c> child, and the scope id it was written under (the
    /// directory name is the id after the segment check, which rewrites some characters).
    /// </returns>
    private IEnumerable<(string ScopeId, string ScopeDirectory, string ToolResultsDirectory)> SubjectSpillDirectories(
        IReadOnlyList<string> allConversationIds, PlanRunTraces planRuns)
    {
        var root = _options.CurrentValue.AI.ContextManagement.ToolResultStorage.StoragePath;
        foreach (var scopeId in allConversationIds.Concat(planRuns.ToolResultScopeIds).Distinct(StringComparer.Ordinal))
        {
            string safeId;
            try
            {
                safeId = SanitizeSessionSegment(scopeId, nameof(scopeId));
            }
            catch (ArgumentException)
            {
                continue;
            }

            var scopeDirectory = Path.Combine(root, safeId);
            var directory = Path.Combine(scopeDirectory, "tool-results");
            if (Directory.Exists(directory))
                yield return (scopeId, scopeDirectory, directory);
        }
    }

    private static void TryDeleteIfEmpty(string directory)
    {
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            Directory.Delete(directory);
    }
}
