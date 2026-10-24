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

        var root = _options.CurrentValue.AI.ContextManagement.ToolResultStorage.StoragePath;
        var removed = 0;
        foreach (var conversationId in subject.AllConversationIds.Concat(subject.PlanRuns.ToolResultScopeIds))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string safeId;
            try
            {
                safeId = SanitizeSessionSegment(conversationId, nameof(subject));
            }
            catch (ArgumentException)
            {
                continue;
            }

            var directory = Path.Combine(root, safeId, "tool-results");
            if (!Directory.Exists(directory))
                continue;

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                File.Delete(file);
                _pageCache.Remove(file);
                removed++;
            }

            Directory.Delete(directory);
            TryDeleteIfEmpty(Path.Combine(root, safeId));
        }

        return Task.FromResult(removed);
    }

    private static void TryDeleteIfEmpty(string directory)
    {
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            Directory.Delete(directory);
    }
}
