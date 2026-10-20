using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Audit;

/// <summary>The full check of <see cref="AuditArchiver"/>: one log, archive and live, end to end.</summary>
/// <remarks>
/// The application checks only where its live files continue the archive, trusting the archive this archiver built.
/// This walks everything under the archiver's own identity, against every witness ever written, so a check runs that
/// nothing on the application's hosts can influence. Schedule it (daily, say) and alert on anything but success. An
/// auditor can run it too.
/// </remarks>
public sealed partial class AuditArchiver
{
    /// <summary>
    /// Walks one log end to end: every archived file from the oldest against every witness, every live file sorting
    /// within the archive compared with its archived copy, then the live chain, as <see cref="WitnessAsync"/> does
    /// without writing a witness.
    /// </summary>
    /// <param name="chain">The log.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Done when the whole log chains; otherwise where it breaks.</returns>
    public async Task<AuditArchiveResult> VerifyAsync(AuditChainLocation chain, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chain);
        try
        {
            if (await NotReadyAsync(chain, cancellationToken).ConfigureAwait(false) is { } notReady)
                return notReady;

            var state = await ReadArchiveAsync(chain, cancellationToken).ConfigureAwait(false);
            if (await VerifyArchivedAsync(chain, state, cancellationToken).ConfigureAwait(false) is { } archivedProblem)
                return StopVerify(chain, archivedProblem);

            if (await VerifyArchivedCopiesAsync(chain, state, cancellationToken).ConfigureAwait(false) is { } copyProblem)
                return StopVerify(chain, copyProblem);

            var walk = await WalkLiveAsync(chain, state, cancellationToken).ConfigureAwait(false);
            if (walk.Problem is not null)
                return StopVerify(chain, walk.Problem);

            _logger.LogInformation(
                "Audit log '{Chain}' verified to sequence {Sequence} ({Archived} archived file(s)).",
                chain.Name, walk.Head.Sequence, state.Files.Count);
            return AuditArchiveResult.Done(chain.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return StopVerify(chain, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Walks the archived files in order against every witness and requires them to end at the tail the live chain
    /// continues. Starts at genesis, or, when retention has already removed the oldest files, at the oldest remaining
    /// record.
    /// </summary>
    private async Task<string?> VerifyArchivedAsync(AuditChainLocation chain, ArchiveState state, CancellationToken cancellationToken)
    {
        var witnessed = await ReadWitnessesAsync(chain, -1, cancellationToken).ConfigureAwait(false);
        (long Sequence, string Hash) head = (-1, HashChainedJsonlWriter.GenesisHash);
        for (var i = 0; i < state.Files.Count; i++)
        {
            var archived = state.Files[i];
            var bytes = await _store.ReadAsync(archived.Path, cancellationToken).ConfigureAwait(false)
                ?? throw new IOException($"Archived file {archived.Path} is listed but cannot be read.");
            var file = new CheckedFile(
                archived.Path, bytes, head,
                AllowPreChainLines: AllowsPreChainLines(archived.Name, head),
                Accepted: state.Accepted.TryGetValue(archived.Name, out var sha) && sha == Sha256Of(bytes));
            if (LinesOf(file) is not { } lines)
                return $"Archived file {archived.Path}: {StrictProblem(bytes)}.";
            if (bytes.Length > 0 && bytes[^1] != (byte)'\n' && !file.Accepted)
                return $"Archived file {archived.Path} does not end with a whole record.";

            if (head.Sequence < 0 && HashChainedJsonlWriter.FirstRecord(lines) is { Sequence: > 0 } first)
            {
                _logger.LogWarning(
                    "Audit log '{Chain}': the archive starts at sequence {Sequence}; earlier records have expired.",
                    chain.Name, first.Sequence);
                head = (first.Sequence - 1, first.PreviousHash);
            }

            if (!HashChainedJsonlWriter.ContinuesChain(
                    lines, head.Sequence, head.Hash, file.AllowPreChainLines, file.Accepted, witnessed, out head, out var reason))
            {
                return $"Archived file {archived.Path} breaks the chain: {reason}";
            }
        }

        return head == state.Tail ? null : $"The archive's walk ends at sequence {head.Sequence}, not at its newest record {state.Tail.Sequence}.";
    }

    /// <summary>Every live file sorting within the archive must be an identical copy of an archived file.</summary>
    private async Task<string?> VerifyArchivedCopiesAsync(AuditChainLocation chain, ArchiveState state, CancellationToken cancellationToken)
    {
        if (state.Files.Count == 0)
            return null;

        var newest = LivePathOf(chain, state.Files[^1].Name);
        foreach (var path in chain.Segments.Ordered().Where(p => chain.Segments.Compare(p, newest) <= 0))
        {
            var name = Path.GetFileName(path);
            if (state.Files.FirstOrDefault(f => f.Name == name) is not { } copy)
                return $"Live file {name} sorts within the archive but the archive has no copy of it.";

            byte[] liveBytes;
            try
            {
                liveBytes = await ReadLiveAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                continue; // deleted by a run since the listing
            }

            var archivedBytes = await _store.ReadAsync(copy.Path, cancellationToken).ConfigureAwait(false) ?? [];
            if (!liveBytes.AsSpan().SequenceEqual(archivedBytes))
                return $"Live file {name} differs from its archived copy.";
        }

        return null;
    }

    private AuditArchiveResult StopVerify(AuditChainLocation chain, string problem)
    {
        _logger.LogCritical("Audit log '{Chain}' FAILED full verification: {Problem}", chain.Name, problem);
        return AuditArchiveResult.Stopped(chain.Name, 0, problem);
    }
}
