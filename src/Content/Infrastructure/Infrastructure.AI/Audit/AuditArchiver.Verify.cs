using System.Text;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Audit;

/// <summary>The full check of <see cref="AuditArchiver"/>: one log, archive and live, from genesis to its newest record.</summary>
/// <remarks>
/// The application checks only where its live files continue the archive, trusting the archive this archiver built.
/// This walks everything, under the archiver's own identity, so a check runs that nothing on the application's hosts
/// can influence. Schedule it (daily, say) and alert on anything but success. An auditor can run it too.
/// </remarks>
public sealed partial class AuditArchiver
{
    /// <summary>
    /// Walks one log end to end: every archived file from genesis, every live copy of an archived file compared with
    /// it, then the live files, up to their last whole line.
    /// </summary>
    /// <param name="chain">The log.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Done when the whole log chains; otherwise where it breaks.</returns>
    public async Task<AuditArchiveResult> VerifyAsync(AuditChainLocation chain, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chain);
        if (NotReady(chain) is { } notReady)
            return notReady;

        try
        {
            var archived = chain.Segments.ArchivedUnder(_archiveRoot).Ordered();
            var live = chain.Segments.Ordered();
            (long Sequence, string Hash) head = (-1, HashChainedJsonlWriter.GenesisHash);

            var files = new List<(string Path, bool Archived)>(archived.Select(p => (p, true)));
            foreach (var path in live)
            {
                var name = Path.GetFileName(path);
                var copy = archived.FirstOrDefault(p => string.Equals(Path.GetFileName(p), name, StringComparison.Ordinal));
                if (copy is not null)
                {
                    var liveBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                    var archivedBytes = await File.ReadAllBytesAsync(copy, cancellationToken).ConfigureAwait(false);
                    if (!liveBytes.AsSpan().SequenceEqual(archivedBytes))
                        return StopVerify(chain, $"Live file {name} differs from its archived copy.");
                }
                else if (archived.Count > 0 && chain.Segments.Compare(path, archived[^1]) <= 0)
                {
                    return StopVerify(chain, $"Live file {name} sorts within the archive but the archive has no copy of it.");
                }
                else
                {
                    files.Add((path, false));
                }
            }

            for (var i = 0; i < files.Count; i++)
            {
                var text = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(files[i].Path, cancellationToken).ConfigureAwait(false));
                // A live file can be mid-append: only its whole lines count. An archived one must be whole.
                var end = text.LastIndexOf('\n') + 1;
                if (files[i].Archived && end != text.Length)
                    return StopVerify(chain, $"Archived file {Path.GetFileName(files[i].Path)} does not end with a whole record.");

                if (!HashChainedJsonlWriter.ContinuesChain(
                        text[..end].Split('\n'), head.Sequence, head.Hash, allowPreChainLines: i == 0, out head, out var reason))
                {
                    var where = files[i].Archived ? "Archived" : "Live";
                    return StopVerify(chain, $"{where} file {Path.GetFileName(files[i].Path)} breaks the chain: {reason}");
                }
            }

            _logger.LogInformation(
                "Audit log '{Chain}' verified from genesis to sequence {Sequence} ({Archived} archived file(s)).",
                chain.Name, head.Sequence, archived.Count);
            return AuditArchiveResult.Done(chain.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StopVerify(chain, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private AuditArchiveResult StopVerify(AuditChainLocation chain, string problem)
    {
        _logger.LogCritical("Audit log '{Chain}' FAILED full verification: {Problem}", chain.Name, problem);
        return AuditArchiveResult.Stopped(chain.Name, 0, problem);
    }
}
