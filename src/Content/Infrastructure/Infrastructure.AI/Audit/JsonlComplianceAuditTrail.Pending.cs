using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Application.AI.Common.OpenTelemetry.Metrics;
using Application.Core.Compliance;
using Domain.AI.Telemetry.Conventions;
using Domain.Common;
using Domain.Common.Config;
using Infrastructure.AI.Helpers;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Audit;

/// <summary>
/// The pending folder (#995): where a compliance record that could not be appended is held until
/// <see cref="ReplayPendingAsync"/> writes it to the chain.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why.</strong> An erasure, export or removal is carried out whether or not its record can be written, because a
/// broken trail must not refuse a person their request. Before #995 a record the chain refused (busy for longer than the
/// writer's ten-second wait, a full disk, a folder that cannot be written) survived only as an error log line, deleted with
/// the logs after 30 days.
/// </para>
/// <para>
/// <strong>What is held.</strong> One file per record, <c>{UTC ticks}-{guid}.pending</c>, in
/// <c>{Erasure:AuditStoragePath}/compliance-pending</c>: a sibling of the day-file folder, so it is never read as part of
/// the chain or archived, and covered by the same overlap rules as the trail's own folder. A file holds the exact line the
/// chain refused, after the operator was replaced by their reference, so it holds nothing the trail would not.
/// </para>
/// <para>
/// <strong>Crash safety.</strong> A file is written under a <c>.tmp</c> name, flushed to disk, then renamed, before
/// <see cref="RecordAsync"/> returns; replay reads only renamed files. The rename itself is not forced to disk (.NET cannot
/// flush a folder), so on Linux a power cut in the moment after it can still lose the file; a process crash cannot. A
/// <c>.tmp</c> whose write failed is deleted; one left by a crash mid-write is never read or removed.
/// </para>
/// <para>
/// <strong>Replay and duplicates.</strong> A pass takes an exclusive lock file in the pending folder (skipping the pass
/// when another process holds it, so scaled-out hosts sharing the folder never replay one record twice), appends the held
/// records oldest first, and removes each file only once its append succeeded. A crash between the two leaves a file whose
/// record is already in the chain, so a pass first looks for every held line in the chain (from the day before the oldest
/// file on) and only removes those it finds. The correlation id is not unique to a record (an attempt and its outcome share
/// it), so the match is on the whole line, timestamp included. A pass stops at the first append that fails, and the next
/// pass retries from there. A file that cannot be read (another account owns it, another process holds it open) is left
/// for the next pass and the files after it go ahead; a file that reads but is not a record is set aside as
/// <c>.unreadable</c>, so neither stops the queue.
/// </para>
/// <para>
/// <strong>Who can add records.</strong> Replay appends whatever record a held file contains, so anything that can write
/// the pending folder can add a record to the trail, just as anything that can write the trail's own folder can append a
/// linked line to it (the chain's hashes are not keyed). Both folders are made owner-only; keep both out of reach of
/// anything but the application's own account.
/// </para>
/// <para>
/// <strong>Order.</strong> The chain is in append order. A replayed record keeps its own <c>timestamp</c> and
/// <c>correlation_id</c> but lands after records appended while it was held, so a reader orders requests by
/// <c>timestamp</c>, not by position in the chain.
/// </para>
/// <para>
/// <strong>What it does not cover.</strong> The folder is on the same volume as the trail by default, so a full disk or a
/// read-only volume usually refuses the held file too. Then the record is lost: logged as critical and counted with
/// outcome <c>lost</c> on <see cref="ComplianceMetrics.AuditWriteFailures"/>. The lock file has the chain lock's limits
/// (see <see cref="HashChainedJsonlWriter"/>).
/// </para>
/// </remarks>
public sealed partial class JsonlComplianceAuditTrail
{
    private const string PendingExtension = ".pending";
    private const string TemporaryExtension = ".tmp";
    private const string SetAsideExtension = ".unreadable";
    private const string ReplayLockName = ".replay.lock";
    private const string DayFormat = "yyyy-MM-dd";
    private const string UnknownAction = "unknown";

    private static readonly SearchValues<char> FramingChars = SearchValues.Create("\t\n\r");

    private readonly string _pendingFolder;

    /// <summary>Makes the unique part of a held file's name. Replaced only by tests that need to predict the name.</summary>
    internal Func<Guid> NewHeldId { get; init; } = Guid.NewGuid;

    /// <summary>Gets the folder that holds records not yet appended to the trail.</summary>
    public string PendingLocation => _pendingFolder;

    /// <summary>
    /// Appends every held record to the trail, oldest first, and removes each once it is in the trail. Run on start and
    /// every minute by <see cref="ComplianceAuditReplayService"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the pass did.</returns>
    public async Task<PendingRecordsReplay> ReplayPendingAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_pendingFolder))
            return PendingRecordsReplay.Nothing;

        var (replayLock, busy) = TryLockPending();
        if (replayLock is null)
            return new PendingRecordsReplay(0, 0, 0, HeldFiles().Count, SkippedBecause: busy);

        await using (replayLock.ConfigureAwait(false))
        {
            var (held, setAside) = ReadHeld();
            if (held.Count == 0)
                return new PendingRecordsReplay(0, 0, setAside, HeldFiles().Count, SkippedBecause: null);

            var inTrail = await AlreadyInTrailAsync(held, cancellationToken).ConfigureAwait(false);
            var (written, alreadyInTrail) = await AppendHeldAsync(held, inTrail, cancellationToken).ConfigureAwait(false);
            return new PendingRecordsReplay(written, alreadyInTrail, setAside, HeldFiles().Count, SkippedBecause: null);
        }
    }

    private static string PendingFolder(AppConfig config) =>
        Path.Combine(config.AI.Erasure.AuditStoragePath, "compliance-pending");

    private Result Hold(string action, string payload, IReadOnlyList<string> reasons)
    {
        var why = string.Join("; ", reasons);
        var held = WritePending(payload);
        if (held.IsSuccess)
        {
            CountFailure(action, ComplianceConventions.OutcomeValues.Pending);
            _logger.LogWarning(
                "Compliance record not yet written to {AuditLocation}; held in {PendingLocation} to be written by replay.",
                _folder, _pendingFolder);
            return Result.Fail($"{why} The record is held in {_pendingFolder} and will be written to the trail by replay.");
        }

        var heldWhy = string.Join("; ", held.Errors);
        CountFailure(action, ComplianceConventions.OutcomeValues.Lost);
        _logger.LogCritical(
            "Compliance record not written to {AuditLocation} and could not be held in {PendingLocation}: {Reason}. " +
            "Only the caller's error log line remains.",
            _folder, _pendingFolder, heldWhy);
        return Result.Fail($"{why} The record could not be held for replay either: {heldWhy}");
    }

    private Result WritePending(string payload)
    {
        var name = string.Create(CultureInfo.InvariantCulture, $"{_clock.GetUtcNow().UtcTicks:D19}-{NewHeldId():N}");
        var temporary = Path.Combine(_pendingFolder, name + TemporaryExtension);
        var created = false;
        try
        {
            OwnerOnlyDirectoryHelper.Create(_pendingFolder, _logger);

            // Flushed to disk under a temporary name, then renamed: replay never reads a half-written record.
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                stream.Write(Encoding.UTF8.GetBytes(payload));
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, Path.Combine(_pendingFolder, name + PendingExtension), overwrite: false);
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A half-written record is no record, and nothing else would ever remove it. Only this call's own file, though.
            if (created)
                DeleteTemporary(temporary);
            return Result.Fail(ex.Message);
        }
    }

    private void DeleteTemporary(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remove {TemporaryFile}; replay never reads it, so it can be deleted by hand.", path);
        }
    }

    /// <summary>The replay lock, or why it could not be taken: usually another pass holds it, here or elsewhere.</summary>
    private (FileStream? Lock, string? Busy) TryLockPending()
    {
        try
        {
            var path = Path.Combine(_pendingFolder, ReplayLockName);
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
        Directory.GetFiles(_pendingFolder, "*" + PendingExtension)
            .Where(path => path.EndsWith(PendingExtension, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

    private (List<HeldRecord> Held, int SetAside) ReadHeld()
    {
        var held = new List<HeldRecord>();
        var setAside = 0;
        foreach (var path in HeldFiles())
        {
            try
            {
                if (ReadOne(path) is { } record)
                    held.Add(record);
                else
                    setAside++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file this pass cannot touch must not hold back the others; it is tried again next pass.
                _logger.LogWarning(ex, "Held compliance record {PendingFile} could not be read; it is left for the next pass.", path);
            }
        }

        return (held, setAside);
    }

    /// <summary>The held record in <paramref name="path"/>, or null once a file that is no record has been set aside.</summary>
    private HeldRecord? ReadOne(string path)
    {
        var payload = File.ReadAllText(path, Encoding.UTF8);
        if (ActionOf(payload) is { } action)
            return new HeldRecord(path, payload, HeldOn(path), action);

        // Not a record the chain would accept: keeping it in the queue would stop every record after it.
        var setAsideAs = SetAsideName(path);
        File.Move(path, setAsideAs, overwrite: false);
        CountFailure(UnknownAction, ComplianceConventions.OutcomeValues.Lost);
        _logger.LogCritical(
            "Held compliance record {PendingFile} is not a record and was set aside as {SetAsideFile}; it is not in the trail.",
            path, setAsideAs);
        return null;
    }

    // An earlier file set aside under the same name (copied back to retry it, say) must not stop this one being set aside.
    private static string SetAsideName(string path)
    {
        var name = Path.ChangeExtension(path, SetAsideExtension);
        return File.Exists(name) ? Path.ChangeExtension(path, $".{Guid.NewGuid():N}{SetAsideExtension}") : name;
    }

    private async Task<HashSet<string>> AlreadyInTrailAsync(List<HeldRecord> held, CancellationToken cancellationToken)
    {
        var wanted = held.Select(record => record.Payload).ToHashSet(StringComparer.Ordinal);
        // A record appended by an earlier pass went to a day-file no earlier than the day it was held; one day of margin
        // for a host whose clock ran behind the one that held it.
        var oldest = held.Min(record => record.HeldOn);
        var from = oldest > DateOnly.MinValue ? oldest.AddDays(-1) : oldest;
        var found = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var payload in _chain.ReadPayloadsAsync(path => MayHoldReplayed(path, from), cancellationToken)
                           .ConfigureAwait(false))
        {
            if (wanted.Contains(payload))
                found.Add(payload);
        }

        return found;
    }

    private async Task<(int Written, int AlreadyInTrail)> AppendHeldAsync(
        List<HeldRecord> held, HashSet<string> inTrail, CancellationToken cancellationToken)
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

            var appended = await _chain.AppendAsync(record.Payload, cancellationToken).ConfigureAwait(false);
            if (!appended.IsSuccess)
            {
                _logger.LogWarning(
                    "Replay of held compliance records into {AuditLocation} stopped: {Reason}. The next pass retries.",
                    _folder, string.Join("; ", appended.Errors));
                break;
            }

            // Removed only after the append: a crash in between leaves a file the next pass finds in the trail.
            written++;
            ComplianceMetrics.AuditRecordsReplayed.Add(1,
                new KeyValuePair<string, object?>(ComplianceConventions.Action, KnownAction(record.Action)));
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
            _logger.LogWarning(ex, "Could not remove held compliance record {PendingFile} after it was written.", path);
        }
    }

    private static void CountFailure(string action, string outcome) =>
        ComplianceMetrics.AuditWriteFailures.Add(1,
            new KeyValuePair<string, object?>(ComplianceConventions.Action, action),
            new KeyValuePair<string, object?>(ComplianceConventions.Outcome, outcome));

    /// <summary>
    /// A held file's action as a metric tag: one of the three shipped requests, so file content can never add a time series.
    /// A failure counted as it happens is tagged with the entry's own action instead, which comes from code; a deployment
    /// that adds a request of its own sees it there under its own name, and under <c>unknown</c> only once replayed.
    /// </summary>
    private static string KnownAction(string action) =>
        action is AuditedErasure.AuditAction or AuditedExport.AuditAction or AuditedDocumentRemoval.AuditAction
            ? action
            : UnknownAction;

    /// <summary>The record's action when <paramref name="payload"/> is one line the chain accepts, otherwise null.</summary>
    private static string? ActionOf(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload) || payload.AsSpan().IndexOfAny(FramingChars) >= 0)
            return null;

        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("action", out var action)
                && action.ValueKind == JsonValueKind.String
                    ? action.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
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

    // A file that is not a day-file is read too: only a day-file dated before the margin is skipped.
    private static bool MayHoldReplayed(string path, DateOnly from) =>
        !DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(path), DayFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var day)
        || day >= from;

    private sealed record HeldRecord(string FilePath, string Payload, DateOnly HeldOn, string Action);
}
