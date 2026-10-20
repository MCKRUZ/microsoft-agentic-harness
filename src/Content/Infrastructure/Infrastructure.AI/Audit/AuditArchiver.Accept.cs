using Domain.AI.Audit;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Audit;

/// <summary>The accept step of <see cref="AuditArchiver"/>: letting an investigated, honest break through.</summary>
public sealed partial class AuditArchiver
{
    /// <summary>
    /// Records, in the write-once archive, that the live file <paramref name="fileName"/> may be archived although
    /// lines in it are not records: for a break someone has investigated and found honest, typically a line torn by a
    /// crash mid-write. Covers only the file's bytes as they are now, named by <paramref name="sha256"/>, which the
    /// operator copies from the file they inspected, and only when <paramref name="linesSteppedOver"/> is exactly how
    /// many lines after its first record are not records, so lines placed there cannot slip through with a crash's.
    /// Lines before its first record (the drift log's oldest day-files hold only such lines) are taken as history from
    /// before hash chaining, pinned by the SHA-256. It covers nothing written or changed afterwards.
    /// </summary>
    /// <param name="chain">The log.</param>
    /// <param name="fileName">The live file's name.</param>
    /// <param name="sha256">The lowercase hex SHA-256 of the file's bytes, as inspected.</param>
    /// <param name="linesSteppedOver">How many lines the operator saw are not records.</param>
    /// <param name="reason">Why it is accepted, kept with the acceptance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Done, or why it was not recorded.</returns>
    public async Task<AuditArchiveResult> AcceptAsync(
        AuditChainLocation chain, string fileName, string sha256, int linesSteppedOver, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        try
        {
            if (await NotReadyAsync(chain, cancellationToken).ConfigureAwait(false) is { } notReady)
                return notReady;

            var live = chain.Segments.Ordered().FirstOrDefault(p => Path.GetFileName(p) == fileName);
            if (live is null)
                return Stop(chain, 0, $"The log has no live file {fileName}.");

            var bytes = await ReadLiveAsync(live, cancellationToken).ConfigureAwait(false);
            var actual = Sha256Of(bytes);
            if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
                return Stop(chain, 0, $"{fileName} now hashes to {actual}, not {sha256}: it changed since it was inspected.");

            var skipped = NotRecords(bytes);
            if (skipped.Count != linesSteppedOver)
            {
                return Stop(chain, 0,
                    $"{fileName} has {skipped.Count} line(s) after its first record that are not records " +
                    $"(line {string.Join(", ", skipped)}), not {linesSteppedOver}. Inspect every one: readers do not serve " +
                    "them, but a line placed there means records may have been tampered with.");
            }

            var acceptance = new AuditAcceptance(fileName, actual, skipped, reason, _clock.GetUtcNow());
            var path = $"{FolderOf(chain)}/{AuditAcceptance.Folder}/{fileName}.json";
            if (await _store.PutIfAbsentAsync(path, acceptance.ToBytes(), cancellationToken).ConfigureAwait(false) == AuditArchivePut.Different)
                return Stop(chain, 0, $"{fileName} was already accepted, with other bytes or another reason; that acceptance stands.");

            _logger.LogWarning(
                "Audit log '{Chain}': {File} (SHA-256 {Sha256}) accepted for archiving, stepping over line(s) {Lines}: {Reason}",
                chain.Name, fileName, actual, string.Join(", ", skipped), reason);
            return AuditArchiveResult.Done(chain.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Stop(chain, 0, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The line numbers (from 1), as the application reads the file, of non-blank lines after its first record that are
    /// not whole records: what readers drop and the archiver steps over. Lines before the first record are history
    /// from before hash chaining, pinned by the file's SHA-256 and served as they are.
    /// </summary>
    private static IReadOnlyList<int> NotRecords(byte[] bytes)
    {
        var lines = ReadLikeTheApplication(bytes);
        var numbers = new List<int>();
        var recordsBegun = false;
        for (var i = 0; i < lines.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;

            var isRecord = HashChainedJsonlWriter.IsSelfConsistentRecord(lines[i]);
            recordsBegun |= isRecord;
            if (recordsBegun && !isRecord)
                numbers.Add(i + 1);
        }

        return numbers;
    }
}
