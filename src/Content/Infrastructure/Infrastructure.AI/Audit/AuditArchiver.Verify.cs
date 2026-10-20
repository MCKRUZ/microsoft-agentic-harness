using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Audit;

/// <summary>The full check of <see cref="AuditArchiver"/>: one log, archive and live, end to end.</summary>
/// <remarks>
/// The application checks only where its live files continue the archive, trusting the archive this archiver built.
/// This walks everything under the archiver's own identity, so a check runs that nothing on the application's hosts
/// can influence. Schedule it (daily, say) and alert on anything but success. An auditor can run it too.
/// </remarks>
public sealed partial class AuditArchiver
{
    /// <summary>
    /// Walks one log end to end: every archived file from the oldest, every live copy of an archived file compared
    /// with it, then the live chain against the witnesses, as <see cref="WitnessAsync"/> does without writing one.
    /// </summary>
    /// <param name="chain">The log.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Done when the whole log chains; otherwise where it breaks.</returns>
    public async Task<AuditArchiveResult> VerifyAsync(AuditChainLocation chain, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chain);
        if (await NotReadyAsync(chain, cancellationToken).ConfigureAwait(false) is { } notReady)
            return notReady;

        try
        {
            var state = await ReadArchiveAsync(chain, cancellationToken).ConfigureAwait(false);
            if (await VerifyArchivedAsync(chain, state, cancellationToken).ConfigureAwait(false) is { } archivedProblem)
                return StopVerify(chain, archivedProblem);

            foreach (var path in chain.Segments.Ordered())
            {
                var name = Path.GetFileName(path);
                if (state.Files.FirstOrDefault(f => f.Name == name) is not { } copy)
                    continue;

                var archivedBytes = await _store.ReadAsync(copy.Path, cancellationToken).ConfigureAwait(false) ?? [];
                if (!(await ReadLiveAsync(path, cancellationToken).ConfigureAwait(false)).AsSpan().SequenceEqual(archivedBytes))
                    return StopVerify(chain, $"Live file {name} differs from its archived copy.");
            }

            var walk = await WalkLiveAsync(chain, state, cancellationToken).ConfigureAwait(false);
            if (walk.Problem is not null)
                return StopVerify(chain, walk.Problem);

            _logger.LogInformation(
                "Audit log '{Chain}' verified to sequence {Sequence} ({Archived} archived file(s)).",
                chain.Name, walk.Head.Sequence, state.Files.Count);
            return AuditArchiveResult.Done(chain.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StopVerify(chain, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Walks the archived files in order and requires them to end at the tail the live chain continues. Starts at
    /// genesis, or, when retention has already removed the oldest files, at the oldest remaining record.
    /// </summary>
    private async Task<string?> VerifyArchivedAsync(AuditChainLocation chain, ArchiveState state, CancellationToken cancellationToken)
    {
        (long Sequence, string Hash) head = (-1, HashChainedJsonlWriter.GenesisHash);
        for (var i = 0; i < state.Files.Count; i++)
        {
            var file = state.Files[i];
            var bytes = await _store.ReadAsync(file.Path, cancellationToken).ConfigureAwait(false)
                ?? throw new IOException($"Archived file {file.Path} is listed but cannot be read.");
            if (!StrictAuditBytes.TryRead(bytes, out var lines, out var problem))
                return $"Archived file {file.Path}: {problem}.";

            var accepted = state.Accepted.TryGetValue(file.Name, out var sha) && sha == Sha256Of(bytes);
            if (bytes.Length > 0 && bytes[^1] != (byte)'\n' && !accepted)
                return $"Archived file {file.Path} does not end with a whole record.";

            if (head.Sequence < 0 && HashChainedJsonlWriter.FirstRecord(lines) is { Sequence: > 0 } first)
            {
                _logger.LogWarning(
                    "Audit log '{Chain}': the archive starts at sequence {Sequence}; earlier records have expired.",
                    chain.Name, first.Sequence);
                head = (first.Sequence - 1, first.PreviousHash);
            }

            if (!HashChainedJsonlWriter.ContinuesChain(
                    lines, head.Sequence, head.Hash, allowPreChainLines: i == 0, accepted, witnessed: null, out head, out var reason))
            {
                return $"Archived file {file.Path} breaks the chain: {reason}";
            }
        }

        return head == state.Tail ? null : $"The archive's walk ends at sequence {head.Sequence}, not at its newest record {state.Tail.Sequence}.";
    }

    private AuditArchiveResult StopVerify(AuditChainLocation chain, string problem)
    {
        _logger.LogCritical("Audit log '{Chain}' FAILED full verification: {Problem}", chain.Name, problem);
        return AuditArchiveResult.Stopped(chain.Name, 0, problem);
    }
}
