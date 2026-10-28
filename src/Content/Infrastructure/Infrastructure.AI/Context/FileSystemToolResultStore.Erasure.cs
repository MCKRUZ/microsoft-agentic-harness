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
    /// Spills live under <c>{StoragePath}/{scope folder}/tool-results/</c>, where the scope is a conversation, a
    /// planned-task model step, or a planned-task run's own scope for its tool steps (#906), and the folder is named by
    /// <see cref="ScopeDirectoryName(string)"/>. Each id goes through the same segment check a write does before it becomes a
    /// path, so an id can never reach outside the storage root; one the check refuses could never have been written to,
    /// and is skipped. Spills written before #928 are found under their old folder name too (see
    /// <see cref="SubjectSpillDirectories"/>). Each removed file is also evicted from the page cache, or a copy would keep
    /// being served for up to its sliding expiry.
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
    /// The spill directories that exist for one person: for each conversation id and planned-task run scope, its folder
    /// and, if one is left, the folder the store named it before #928. Erasure and export both read through this, so a
    /// copy holds exactly what an erasure deletes. Each id goes through the same segment check a write does; one the check
    /// refuses could never have been written to, and is skipped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An old folder is the id with its case kept, so on Windows or macOS <c>TICKET-42</c>'s old name opens the folder
    /// <c>ticket-42</c> created. It is taken only when the name stored on disk is exactly this id's old name, compared
    /// with case, so a case variant of another person's id does not reach the folder that id created.
    /// </para>
    /// <para>
    /// What this cannot fix: before #928 a second id differing only by case wrote into the first one's folder, and the
    /// files carry no owner. Such a folder is erased and copied for the id that created it, whoever's spills it holds,
    /// and is not found for the other. Old folders get no new writes, so this ends when retention removes them.
    /// </para>
    /// </remarks>
    /// <param name="allConversationIds">The person's conversations and their planned tasks' model steps.</param>
    /// <param name="planRuns">The person's planned-task runs, whose tool steps spill under the run's own scope.</param>
    /// <returns>
    /// Each existing scope directory with its <c>tool-results</c> child, and the scope id it was written under (the
    /// directory name cannot be turned back into the id).
    /// </returns>
    private IEnumerable<(string ScopeId, string ScopeDirectory, string ToolResultsDirectory)> SubjectSpillDirectories(
        IReadOnlyList<string> allConversationIds, PlanRunTraces planRuns)
    {
        var root = _options.CurrentValue.AI.ContextManagement.ToolResultStorage.StoragePath;
        foreach (var scopeId in allConversationIds.Concat(planRuns.ToolResultScopeIds).Distinct(StringComparer.Ordinal))
        {
            string folderName;
            try
            {
                folderName = ScopeDirectoryName(scopeId, nameof(scopeId));
            }
            catch (ArgumentException)
            {
                continue;
            }

            foreach (var scopeDirectory in new[] { Path.Combine(root, folderName), LegacyScopeDirectory(root, scopeId) })
            {
                if (scopeDirectory is null)
                    continue;

                var directory = Path.Combine(scopeDirectory, "tool-results");
                if (Directory.Exists(directory))
                    yield return (scopeId, scopeDirectory, directory);
            }
        }
    }

    /// <summary>
    /// The folder the store named <paramref name="scopeId"/> before #928, if one exists whose stored name is exactly that
    /// name, compared with case; see <see cref="SubjectSpillDirectories"/>.
    /// </summary>
    /// <param name="root">The storage root.</param>
    /// <param name="scopeId">An id that has passed the segment check.</param>
    /// <returns>The old folder's path, or <see langword="null"/> when there is none.</returns>
    private static string? LegacyScopeDirectory(string root, string scopeId)
    {
        if (!Directory.Exists(root))
            return null;

        // The pattern holds no wildcard ([A-Za-z0-9_.~-] only), so this lists at most the folder the name opens; on a
        // case-insensitive file system that folder may have been created under another id's case.
        var legacyName = LegacyScopeDirectoryName(scopeId);
        return Directory.EnumerateDirectories(root, legacyName)
            .FirstOrDefault(path => string.Equals(Path.GetFileName(path), legacyName, StringComparison.Ordinal));
    }

    private static void TryDeleteIfEmpty(string directory)
    {
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            Directory.Delete(directory);
    }
}
