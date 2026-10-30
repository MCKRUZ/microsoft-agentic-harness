using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Telemetry.Conventions;
using Domain.Common;
using Domain.Common.Compliance;
using Infrastructure.AI.Helpers;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Audit;

/// <summary>
/// One audit chain's pending folder (#995, #1001): where a record that could not be appended is held until
/// <see cref="ReplayAsync"/> writes it to the chain. Owned by the chain's <see cref="HashChainedJsonlWriter"/>, which holds
/// a record here whenever its append fails, so every one of the six trails keeps a record it could not write.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why.</strong> An audit record is made when something has already happened: an erasure carried out, a governance
/// decision taken, an outbound request decided. Before #995 (compliance) and #1001 (the other five trails) a record the chain
/// refused (busy for longer than the writer waits, a full disk, a folder that cannot be written) survived only as an error
/// log line, deleted with the logs after 30 days. What the caller does about the failure is unchanged: a trail that refuses
/// the action when its record cannot be written (changes, egress, escalations) still refuses it. Changes and egress hold
/// their decision marked <c>not_carried_out</c>, so replay never writes an allowed decision for a request that never went
/// out. The escalation trail holds only execution reports: the escalation service refuses or re-drives its other records
/// itself, through <see cref="HashChainedJsonlWriter.AppendWithoutHoldingAsync"/>, and holding them too would write them
/// twice.
/// </para>
/// <para>
/// <strong>What is held.</strong> One file per record, <c>{UTC ticks}-{guid}.pending</c>, in the chain's
/// <see cref="AuditChainLocation.PendingPath"/>: a sibling of the day-file folder, so it is never read as part of the chain
/// or archived, and inside the folder the trail is configured in, so the rules that keep that folder apart cover it too. A
/// file holds the exact line the chain refused, after every keyed reference was taken, so it holds nothing the trail would
/// not.
/// </para>
/// <para>
/// <strong>Crash safety.</strong> A file is written under a <c>.tmp</c> name, flushed to disk, then renamed, before the
/// append returns; replay reads only renamed files. The rename itself is not forced to disk (.NET cannot flush a folder), so
/// on Linux a power cut in the moment after it can still lose the file; a process crash cannot. A <c>.tmp</c> whose write
/// failed is deleted; one left by a crash mid-write is never read or removed.
/// </para>
/// <para>
/// <strong>Replay and duplicates.</strong> A pass takes an exclusive lock file in the pending folder (skipping the pass when
/// another process holds it, so scaled-out hosts sharing the folder never replay one record twice), appends the held records
/// oldest first, and removes each file only once its append succeeded. A crash between the two leaves a file whose record is
/// already in the chain, so a pass first looks for every held line in the chain (from the day before the oldest file on)
/// and only removes those it finds. No field of a record is unique to it on every trail (a compliance attempt and its outcome
/// share a correlation id), so the match is on the whole line, timestamp included. Every trail stamps its records to the
/// tick, so two lines are identical only for two records made in the same tick with the same content; replay would keep one.
/// A pass stops at the first append that fails, and the next pass retries from there. A file that cannot be read (another
/// account owns it, another process holds it open) is left for the next pass and the files after it go ahead. It is counted
/// with outcome <c>stuck</c> and logged as an error when it first sticks, not on every pass; once a pass no longer finds it
/// stuck (read, set aside or gone), a later stick is reported again. A file that reads but is not a record of this chain (one
/// line, a JSON object carrying the chain's <see cref="AuditChainLocation.RecordProperty"/>) is set aside as
/// <c>.unreadable</c>, so neither stops the queue.
/// </para>
/// <para>
/// <strong>Who can add records.</strong> Replay appends whatever record a held file contains, so anything that can write the
/// pending folder can add a record to the trail, just as anything that can write the trail's own folder can append a linked
/// line to it (the chain's hashes are not keyed). Both folders are made owner-only; keep both out of reach of anything but
/// the application's own account.
/// </para>
/// <para>
/// <strong>Order.</strong> The chain is in append order. A replayed record keeps its own timestamp but lands after records
/// appended while it was held, so a reader orders records by timestamp, not by position in the chain.
/// </para>
/// <para>
/// <strong>What it does not cover.</strong> The folder is on the same volume as the trail by default, so a full disk or a
/// read-only volume usually refuses the held file too. Then the record is lost: logged as critical and counted with outcome
/// <c>lost</c> on <see cref="AuditMetrics.WriteFailures"/>. The lock file has the chain lock's limits (see
/// <see cref="HashChainedJsonlWriter"/>).
/// </para>
/// </remarks>
[PersonalDataChannel("Holds, in one trail's pending folder, the exact line that trail could not append, until replay writes it to the chain; each trail's store describes what its lines hold and names its pending folder.")]
public sealed partial class PendingAuditRecords
{
    private const string PendingExtension = ".pending";
    private const string TemporaryExtension = ".tmp";

    private static readonly SearchValues<char> FramingChars = SearchValues.Create("\t\n\r");

    private readonly string _chainName;
    private readonly string _recordProperty;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;

    /// <summary>Held files this process has already reported as stuck, so each is counted and logged once, not every pass.</summary>
    private readonly ConcurrentDictionary<string, byte> _reportedStuck = new(StringComparer.Ordinal);

    /// <summary>Initializes the pending folder of one chain.</summary>
    /// <param name="folder">The folder held records are kept in; created when the first record is held.</param>
    /// <param name="chainName">The chain's name, for logs and the <see cref="AuditConventions.Chain"/> tag.</param>
    /// <param name="recordProperty">A property every record of the chain carries; a held file without it is set aside.</param>
    /// <param name="clock">Names held files by the time they were held, so replay keeps them in that order.</param>
    /// <param name="logger">Logger for holds, losses and replay.</param>
    public PendingAuditRecords(string folder, string chainName, string recordProperty, TimeProvider clock, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(chainName);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordProperty);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        Folder = folder;
        _chainName = chainName;
        _recordProperty = recordProperty;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Gets the folder that holds records not yet appended to the chain.</summary>
    public string Folder { get; }

    /// <summary>Makes the unique part of a held file's name. Replaced only by tests that need to predict the name.</summary>
    internal Func<Guid> NewHeldId { get; set; } = Guid.NewGuid;

    /// <summary>
    /// Reads a held file listed by a pass. Replaced only by tests that need the file to change between the listing and the
    /// read, as when another host's pass removes it.
    /// </summary>
    internal Func<string, string> ReadHeldFile { get; set; } = static path => File.ReadAllText(path, Encoding.UTF8);

    /// <summary>
    /// Holds <paramref name="payload"/> for replay, after the chain refused it for <paramref name="reasons"/>.
    /// </summary>
    /// <param name="payload">The exact line the chain refused.</param>
    /// <param name="reasons">Why the append failed.</param>
    /// <returns>
    /// Always a failure, since the record is not in the chain yet: one that says where it waits, or that it could not be held
    /// either.
    /// </returns>
    public Result Hold(string payload, IReadOnlyList<string> reasons)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(reasons);

        var why = string.Join("; ", reasons);
        var held = WritePending(payload);
        if (held.IsSuccess)
        {
            CountFailure(AuditConventions.OutcomeValues.Pending);
            _logger.LogWarning(
                "Audit record for the {AuditChain} trail not yet written ({Reason}); held in {PendingLocation} to be written by replay.",
                _chainName, why, Folder);
            // The folder is logged above, not returned: the reason can reach a caller (an egress or change refusal).
            return Result.Fail($"The {_chainName} audit record could not be appended; it is held and will be written to the trail by replay.");
        }

        var heldWhy = string.Join("; ", held.Errors);
        CountFailure(AuditConventions.OutcomeValues.Lost);
        _logger.LogCritical(
            "Audit record for the {AuditChain} trail not written ({Reason}) and could not be held in {PendingLocation}: " +
            "{HoldReason}. Only the caller's error log line remains.",
            _chainName, why, Folder, heldWhy);
        return Result.Fail($"The {_chainName} audit record could not be appended, and could not be held for replay either.");
    }

    private Result WritePending(string payload)
    {
        var name = string.Create(CultureInfo.InvariantCulture, $"{_clock.GetUtcNow().UtcTicks:D19}-{NewHeldId():N}");
        var temporary = Path.Combine(Folder, name + TemporaryExtension);
        var created = false;
        try
        {
            OwnerOnlyDirectoryHelper.Create(Folder, _logger);

            // Flushed to disk under a temporary name, then renamed: replay never reads a half-written record.
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                stream.Write(Encoding.UTF8.GetBytes(payload));
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, Path.Combine(Folder, name + PendingExtension), overwrite: false);
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

    private void CountFailure(string outcome) =>
        AuditMetrics.WriteFailures.Add(1,
            new KeyValuePair<string, object?>(AuditConventions.Chain, _chainName),
            new KeyValuePair<string, object?>(AuditConventions.Outcome, outcome));
}
