using Domain.AI.Audit;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Audit;

/// <summary>The accept step of <see cref="AuditArchiver"/>: letting an investigated, honest break through.</summary>
public sealed partial class AuditArchiver
{
    /// <summary>
    /// Records, in the write-once archive, that the live file <paramref name="fileName"/> may be archived although a
    /// line in it does not chain: for a break someone has investigated and found honest, typically a line torn by a
    /// crash mid-write. Covers only the file's bytes as they are now, named by <paramref name="sha256"/>, which the
    /// operator copies from the file they inspected; it does not cover anything written or changed afterwards.
    /// </summary>
    /// <param name="chain">The log.</param>
    /// <param name="fileName">The live file's name.</param>
    /// <param name="sha256">The lowercase hex SHA-256 of the file's bytes, as inspected.</param>
    /// <param name="reason">Why it is accepted, kept with the acceptance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Done, or why it was not recorded.</returns>
    public async Task<AuditArchiveResult> AcceptAsync(
        AuditChainLocation chain, string fileName, string sha256, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (await NotReadyAsync(chain, cancellationToken).ConfigureAwait(false) is { } notReady)
            return notReady;

        try
        {
            var live = chain.Segments.Ordered().FirstOrDefault(p => Path.GetFileName(p) == fileName);
            if (live is null)
                return Stop(chain, 0, $"The log has no live file {fileName}.");

            var actual = Sha256Of(await ReadLiveAsync(live, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
                return Stop(chain, 0, $"{fileName} now hashes to {actual}, not {sha256}: it changed since it was inspected.");

            var acceptance = new AuditAcceptance(fileName, actual, reason, _clock.GetUtcNow());
            var path = $"{FolderOf(chain)}/{AuditAcceptance.Folder}/{fileName}.json";
            if (await _store.PutIfAbsentAsync(path, acceptance.ToBytes(), cancellationToken).ConfigureAwait(false) == AuditArchivePut.Different)
                return Stop(chain, 0, $"{fileName} was already accepted, with other bytes or another reason; that acceptance stands.");

            _logger.LogWarning(
                "Audit log '{Chain}': {File} (SHA-256 {Sha256}) accepted for archiving despite a break: {Reason}",
                chain.Name, fileName, actual, reason);
            return AuditArchiveResult.Done(chain.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Stop(chain, 0, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
