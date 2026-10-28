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
        var legacyFolders = LegacyFolderNames(root);
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

            // The set compares with case, so the stored name it returns always equals the old name. Opening the folder by
            // the stored name rather than the old one keeps that comparison the only guard: were the set ever to ignore
            // case, it would return another id's folder on every OS, which the tests catch even on Linux.
            string[] names = legacyFolders.TryGetValue(LegacyScopeDirectoryName(scopeId), out var storedLegacyName)
                ? [folderName, storedLegacyName]
                : [folderName];
            foreach (var name in names)
            {
                var scopeDirectory = Path.Combine(root, name);
                var directory = Path.Combine(scopeDirectory, "tool-results");
                if (Directory.Exists(directory))
                    yield return (scopeId, scopeDirectory, directory);
            }
        }
    }

    /// <summary>
    /// The names of the folders under <paramref name="root"/> as stored on disk, compared with case, for finding a folder
    /// written before #928; see <see cref="SubjectSpillDirectories"/>.
    /// </summary>
    /// <remarks>
    /// Listed once per erasure or export rather than once per id, and compared here in memory, with case, on every OS: on
    /// Windows and macOS the file system itself would match a folder created under another id's case.
    /// </remarks>
    /// <param name="root">The storage root.</param>
    /// <returns>The folder names, compared ordinally.</returns>
    private static HashSet<string> LegacyFolderNames(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateDirectories(root)
                .Select(Path.GetFileName)
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    private static void TryDeleteIfEmpty(string directory)
    {
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            Directory.Delete(directory);
    }
}
