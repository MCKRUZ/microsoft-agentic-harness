using System.Globalization;
using System.Text.Json;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Telemetry.Conventions;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Audit;

/// <summary>The replay half of <see cref="PendingAuditRecords"/>: writes held records into the chain, oldest first.</summary>
public sealed partial class PendingAuditRecords
{
    private const string SetAsideExtension = ".unreadable";
    private const string ReplayLockName = ".replay.lock";
    private const string DayFormat = "yyyy-MM-dd";

    /// <summary>
    /// Appends every held record to <paramref name="chain"/>, oldest first, and removes each once it is in the chain. Run on
    /// start and every minute by <see cref="AuditRecordReplayService"/>.
    /// </summary>
    /// <param name="chain">The chain the records were refused by; its own writer, so both share one in-process lock.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the pass did.</returns>
    public async Task<PendingRecordsReplay> ReplayAsync(HashChainedJsonlWriter chain, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chain);
        if (!Directory.Exists(Folder))
        {
            _reportedStuck.Clear(); // Nothing is held, so nothing is stuck; a folder restored later is news again.
            return PendingRecordsReplay.Nothing;
        }

        var (replayLock, busy) = TryLockPending();
        if (replayLock is null)
            return new PendingRecordsReplay(0, 0, 0, HeldFiles().Count, SkippedBecause: busy);

        await using (replayLock.ConfigureAwait(false))
        {
            var (held, setAside) = ReadHeld();
            if (held.Count == 0)
                return new PendingRecordsReplay(0, 0, setAside, HeldFiles().Count, SkippedBecause: null);

            var inTrail = await AlreadyInTrailAsync(chain, held, cancellationToken).ConfigureAwait(false);
            var (written, alreadyInTrail) = await AppendHeldAsync(chain, held, inTrail, cancellationToken).ConfigureAwait(false);
            return new PendingRecordsReplay(written, alreadyInTrail, setAside, HeldFiles().Count, SkippedBecause: null);
        }
    }

    /// <summary>How many records are held now; zero when the folder does not exist or cannot be listed.</summary>
    /// <returns>The number of held files.</returns>
    public int CountHeld()
    {
        try
        {
            return Directory.Exists(Folder) ? HeldFiles().Count : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not count the held {AuditChain} audit records in {PendingLocation}.", _chainName, Folder);
            return 0;
        }
    }

    /// <summary>The replay lock, or why it could not be taken: usually another pass holds it, here or elsewhere.</summary>
    private (FileStream? Lock, string? Busy) TryLockPending()
    {
        try
        {
            var path = Path.Combine(Folder, ReplayLockName);
            return (new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A sharing violation and a read-only volume are both an IOException, so the reason goes back to be logged
            // instead of every failure being taken for "busy".
            return (null, ex.Message);
        }
    }

    private List<string> HeldFiles() =>
        Directory.GetFiles(Folder, "*" + PendingExtension)
            .Where(path => path.EndsWith(PendingExtension, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

    private (List<HeldRecord> Held, int SetAside) ReadHeld()
    {
        var held = new List<HeldRecord>();
        var setAside = 0;
        var stuckThisPass = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in HeldFiles())
        {
            try
            {
                if (ReadOne(path) is { } record)
                    held.Add(record);
                else
                    setAside++;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Gone since the folder was listed (removed by hand, or by another host's pass): nothing is stuck.
                _logger.LogDebug(ex, "Held {AuditChain} audit record {PendingFile} went away during the pass.", _chainName, path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file this pass cannot touch must not hold back the others; it is tried again next pass.
                stuckThisPass.Add(path);
                ReportStuck(path, ex);
            }
        }

        // One rule for forgetting: only a file stuck in this pass stays reported. Read, set aside or gone, it is news again
        // should it stick later.
        if (!_reportedStuck.IsEmpty)
        {
            foreach (var path in _reportedStuck.Keys.Where(path => !stuckThisPass.Contains(path)))
                _reportedStuck.TryRemove(path, out _);
        }

        return (held, setAside);
    }

    /// <summary>
    /// Counts and logs a held file that cannot be read or set aside when it first sticks, not on every pass: counted each
    /// minute, the number would measure how long it has been stuck rather than how many records are missing from the trail.
    /// </summary>
    private void ReportStuck(string path, Exception ex)
    {
        if (!_reportedStuck.TryAdd(path, 0))
        {
            _logger.LogDebug("Held {AuditChain} audit record {PendingFile} still cannot be read or set aside.", _chainName, path);
            return;
        }

        CountFailure(AuditConventions.OutcomeValues.Stuck);
        _logger.LogError(ex,
            "Held {AuditChain} audit record {PendingFile} cannot be read or set aside, so it cannot be written to the trail; " +
            "every pass retries it, and this is reported until a pass finds it no longer stuck. Check that the application's " +
            "account can read and rename it.",
            _chainName, path);
    }

    /// <summary>The held record in <paramref name="path"/>, or null once a file that is no record has been set aside.</summary>
    private HeldRecord? ReadOne(string path)
    {
        var payload = ReadHeldFile(path);
        if (IsRecord(payload))
            return new HeldRecord(path, payload, HeldOn(path));

        // Not a record the chain would accept: keeping it in the queue would stop every record after it.
        var setAsideAs = SetAsideName(path);
        File.Move(path, setAsideAs, overwrite: false);
        CountFailure(AuditConventions.OutcomeValues.Lost);
        _logger.LogCritical(
            "Held {AuditChain} audit record {PendingFile} is not a record and was set aside as {SetAsideFile}; it is not in the trail.",
            _chainName, path, setAsideAs);
        return null;
    }

    // An earlier file set aside under the same name (copied back to retry it, say) must not stop this one being set aside.
    private static string SetAsideName(string path)
    {
        var name = Path.ChangeExtension(path, SetAsideExtension);
        return File.Exists(name) ? Path.ChangeExtension(path, $".{Guid.NewGuid():N}{SetAsideExtension}") : name;
    }

    private static async Task<HashSet<string>> AlreadyInTrailAsync(
        HashChainedJsonlWriter chain, List<HeldRecord> held, CancellationToken cancellationToken)
    {
        var wanted = held.Select(record => record.Payload).ToHashSet(StringComparer.Ordinal);
        // A record appended by an earlier pass went to a day-file no earlier than the day it was held; one day of margin
        // for a host whose clock ran behind the one that held it.
        var oldest = held.Min(record => record.HeldOn);
        var from = oldest > DateOnly.MinValue ? oldest.AddDays(-1) : oldest;
        var found = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var payload in chain.ReadPayloadsAsync(path => MayHoldReplayed(path, from), cancellationToken)
                           .ConfigureAwait(false))
        {
            if (wanted.Contains(payload))
                found.Add(payload);
        }

        return found;
    }

    private async Task<(int Written, int AlreadyInTrail)> AppendHeldAsync(
        HashChainedJsonlWriter chain, List<HeldRecord> held, HashSet<string> inTrail, CancellationToken cancellationToken)
    {
        var written = 0;
        var alreadyInTrail = 0;
        foreach (var record in held)
        {
            if (inTrail.Contains(record.Payload))
            {
                alreadyInTrail++;
                Remove(record.FilePath);
                continue;
            }

            // Never held again on failure: the record already is.
            var appended = await chain.AppendWithoutHoldingAsync(record.Payload, cancellationToken).ConfigureAwait(false);
            if (!appended.IsSuccess)
            {
                _logger.LogWarning(
                    "Replay of held audit records into the {AuditChain} trail stopped: {Reason}. The next pass retries.",
                    _chainName, string.Join("; ", appended.Errors));
                break;
            }

            // Removed only after the append: a crash in between leaves a file the next pass finds in the trail.
            written++;
            AuditMetrics.RecordsReplayed.Add(1, new KeyValuePair<string, object?>(AuditConventions.Chain, _chainName));
            Remove(record.FilePath);
        }

        return (written, alreadyInTrail);
    }

    private void Remove(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Its record is in the trail; the next pass finds it there and removes the file without appending it again.
            _logger.LogWarning(ex, "Could not remove held {AuditChain} audit record {PendingFile} after it was written.",
                _chainName, path);
        }
    }

    /// <summary>Whether <paramref name="payload"/> is one line the chain accepts and every trail writes: a JSON object.</summary>
    private static bool IsRecord(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload) || payload.AsSpan().IndexOfAny(FramingChars) >= 0)
            return false;

        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The UTC day a file was held on, from its name, or <see cref="DateOnly.MinValue"/> when the name has none.</summary>
    private static DateOnly HeldOn(string path)
    {
        var name = Path.GetFileName(path);
        return name.Length >= 19
            && long.TryParse(name.AsSpan(0, 19), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            && ticks <= DateTime.MaxValue.Ticks
                ? DateOnly.FromDateTime(new DateTime(ticks, DateTimeKind.Utc))
                : DateOnly.MinValue;
    }

    // A file that is not a day-file (the earlier single file, say) is read too: only a day-file dated before the margin is skipped.
    private static bool MayHoldReplayed(string path, DateOnly from) =>
        !DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(path), DayFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var day)
        || day >= from;

    private sealed record HeldRecord(string FilePath, string Payload, DateOnly HeldOn);
}
