using System.Diagnostics.Metrics;
using System.Text.Json;
using Application.Core.Compliance;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config;
using Domain.Common.Models;
using FluentAssertions;
using Infrastructure.AI.Audit;
using Infrastructure.AI.Tests.Changes.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Infrastructure.AI.Tests.Audit;

/// <summary>
/// A compliance record the trail cannot append is held and written in later (#995), instead of surviving only as a log
/// line that is deleted with the logs.
/// </summary>
/// <remarks>
/// The trail is made unwritable the way an unwritable folder would make it: a file stands where its day-file folder
/// belongs, so every append fails at once rather than after the chain lock's ten-second wait.
/// </remarks>
public sealed class JsonlComplianceAuditTrailPendingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"compliance-pending-{Guid.NewGuid():N}");
    private readonly HmacAuditPseudonymizer _pseudonyms = AuditPseudonyms.Create();

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string Folder => Path.Combine(_root, "compliance");

    private string Pending => Path.Combine(_root, "compliance-pending");

    private JsonlComplianceAuditTrail NewTrail() => new(
        new TestConfig.StaticOptionsMonitor<AppConfig>(new AppConfig { AI = new() { Erasure = new() { AuditStoragePath = _root } } }),
        TimeProvider.System,
        _pseudonyms,
        NullLogger<JsonlComplianceAuditTrail>.Instance);

    private static AuditEntry Entry(string phase, string executorId = "data-subject") => new()
    {
        RequestType = "EraseMyDataCommand",
        Action = AuditedErasure.AuditAction,
        ExecutorId = executorId,
        CorrelationId = "corr-1",
        Timestamp = DateTimeOffset.UnixEpoch,
        Outcome = AuditOutcome.Attempted,
        Metadata = new Dictionary<string, string> { ["phase"] = phase },
    };

    private void BreakTheTrail()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Folder, "not a folder");
    }

    private void RepairTheTrail() => File.Delete(Folder);

    private string[] HeldFiles() =>
        Directory.Exists(Pending) ? Directory.GetFiles(Pending, "*.pending").Order(StringComparer.Ordinal).ToArray() : [];

    private string[] TrailPhases() => Directory.GetFiles(Folder, "*.jsonl").Order(StringComparer.Ordinal)
        .SelectMany(File.ReadAllLines).Where(l => l.Length > 0)
        .Select(l => JsonDocument.Parse(HashChainedJsonlWriter.ExtractPayload(l)).RootElement
            .GetProperty("metadata").GetProperty("phase").GetString()!)
        .ToArray();

    [Fact]
    public async Task ReplayPendingAsync_ARecordThatFailedOnce_EndsUpInTheVerifiedTrail()
    {
        BreakTheTrail();
        using (var first = NewTrail())
            (await first.RecordAsync(Entry("attempt"), ErasureActor.DataSubject, CancellationToken.None)).IsSuccess.Should().BeFalse();
        RepairTheTrail();

        // The next start replays what the last one could not write.
        using var second = NewTrail();
        var pass = await second.ReplayPendingAsync(CancellationToken.None);

        pass.Written.Should().Be(1);
        pass.Remaining.Should().Be(0);
        HeldFiles().Should().BeEmpty();
        var verified = await second.VerifyChainAsync(checkpoint: null, CancellationToken.None);
        verified.IsValid.Should().BeTrue();
        verified.VerifiedCount.Should().Be(1, "the erasure's proof must reach the trail even though the first append failed");
    }

    [Fact]
    public async Task RecordAsync_TheChainCannotBeWritten_HoldsTheRecordAndSaysWhere()
    {
        BreakTheTrail();
        using var trail = NewTrail();

        var result = await trail.RecordAsync(Entry("attempt"), ErasureActor.DataSubject, CancellationToken.None);

        result.IsSuccess.Should().BeFalse("the record is not in the trail yet");
        result.Errors.Single().Should().Contain("held").And.NotContain(Pending, "the folder is in the host log, not the reason");
        HeldFiles().Should().ContainSingle();
        Directory.GetFiles(Pending, "*.tmp").Should().BeEmpty("a held record is renamed into place once flushed");
    }

    [Fact]
    public async Task RecordAsync_TheAppendThrows_HoldsTheRecordInsteadOfLettingItEscape()
    {
        var trail = NewTrail();
        trail.Dispose(); // As at shutdown: the writer's own lock is gone, so the append throws rather than failing.

        var result = await trail.RecordAsync(Entry("attempt"), ErasureActor.DataSubject, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        HeldFiles().Should().ContainSingle("the request this records has already happened");
    }

    [Fact]
    public async Task RecordAsync_ANamedOperatorsHeldRecord_HoldsOnlyTheirReference()
    {
        BreakTheTrail();
        using var trail = NewTrail();

        await trail.RecordAsync(Entry("attempt", "dpo-operator-9"), ErasureActor.Operator("dpo-operator-9"), CancellationToken.None);

        var held = File.ReadAllText(HeldFiles().Single());
        held.Should().NotContain("dpo-operator-9", "the pending folder must hold nothing the trail would not");
        held.Should().Contain(_pseudonyms.Person("dpo-operator-9"));
    }

    [Fact]
    public async Task RecordAsync_TheRecordCannotBeHeldEither_ReturnsAFailureThatSaysSo()
    {
        BreakTheTrail();
        File.WriteAllText(Pending, "not a folder either");
        using var trail = NewTrail();

        var result = await trail.RecordAsync(Entry("attempt"), ErasureActor.DataSubject, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Single().Should().Contain("could not be held");
    }

    [Fact]
    public async Task ReplayPendingAsync_ACrashAfterTheAppendBeforeTheRemoval_DoesNotWriteTheRecordTwice()
    {
        BreakTheTrail();
        using var trail = NewTrail();
        await trail.RecordAsync(Entry("attempt"), ErasureActor.DataSubject, CancellationToken.None);
        var heldFile = HeldFiles().Single();
        var heldLine = File.ReadAllText(heldFile);
        RepairTheTrail();
        await trail.ReplayPendingAsync(CancellationToken.None);

        File.WriteAllText(heldFile, heldLine); // As if the pass had stopped before removing it.
        var pass = await trail.ReplayPendingAsync(CancellationToken.None);

        pass.Written.Should().Be(0);
        pass.AlreadyInTrail.Should().Be(1);
        HeldFiles().Should().BeEmpty();
        TrailPhases().Should().Equal("attempt");
    }

    [Fact]
    public async Task ReplayPendingAsync_AnAttemptAndOutcomeSharingACorrelationId_AreBothWritten()
    {
        BreakTheTrail();
        using var trail = NewTrail();
        await trail.RecordAsync(Entry("attempt"), ErasureActor.DataSubject, CancellationToken.None);
        await trail.RecordAsync(Entry("outcome"), ErasureActor.DataSubject, CancellationToken.None);
        RepairTheTrail();

        var pass = await trail.ReplayPendingAsync(CancellationToken.None);

        pass.Written.Should().Be(2, "the correlation id is shared by a request's records, so it cannot be the duplicate check");
        TrailPhases().Should().Equal("attempt", "outcome");
    }

    [Fact]
    public async Task ReplayPendingAsync_RecordsHeldWhileOthersWereWritten_LandAfterThemKeepingTheirTimestamp()
    {
        BreakTheTrail();
        using var trail = NewTrail();
        await trail.RecordAsync(Entry("attempt"), ErasureActor.DataSubject, CancellationToken.None);
        RepairTheTrail();
        await trail.RecordAsync(Entry("outcome") with { Timestamp = DateTimeOffset.UnixEpoch.AddHours(1) },
            ErasureActor.DataSubject, CancellationToken.None);

        await trail.ReplayPendingAsync(CancellationToken.None);

        TrailPhases().Should().Equal(["outcome", "attempt"], "the chain is in append order; a reader orders by timestamp");
        (await trail.VerifyChainAsync(checkpoint: null, CancellationToken.None)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task ReplayPendingAsync_TheChainStillRefuses_KeepsTheRecordHeld()
    {
        BreakTheTrail();
        using var trail = NewTrail();
        await trail.RecordAsync(Entry("attempt"), ErasureActor.DataSubject, CancellationToken.None);

        var pass = await trail.ReplayPendingAsync(CancellationToken.None);

        pass.Written.Should().Be(0);
        pass.Remaining.Should().Be(1);
        HeldFiles().Should().ContainSingle();
    }

    [Fact]
    public async Task ReplayPendingAsync_AHeldFileThatIsNoRecord_IsSetAsideAndTheRestWritten()
    {
        BreakTheTrail();
        using var trail = NewTrail();
        await trail.RecordAsync(Entry("attempt"), ErasureActor.DataSubject, CancellationToken.None);
        File.WriteAllText(Path.Combine(Pending, "0000000000000000000-torn.pending"), "{\"action\":");
        RepairTheTrail();

        var pass = await trail.ReplayPendingAsync(CancellationToken.None);

        pass.SetAside.Should().Be(1);
        pass.Written.Should().Be(1, "one unreadable file must not stop the records queued behind it");
        File.Exists(Path.Combine(Pending, "0000000000000000000-torn.unreadable")).Should().BeTrue();
    }

    [Fact]
    public async Task ReplayPendingAsync_AnotherPassHoldsTheFolder_SkipsWithoutWriting()
    {
        BreakTheTrail();
        using var trail = NewTrail();
        await trail.RecordAsync(Entry("attempt"), ErasureActor.DataSubject, CancellationToken.None);
        RepairTheTrail();

        PendingRecordsReplay pass;
        using (new FileStream(Path.Combine(Pending, ".replay.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            pass = await trail.ReplayPendingAsync(CancellationToken.None);

        pass.Skipped.Should().BeTrue("two hosts sharing the folder must not replay one record twice");
        pass.Remaining.Should().Be(1);
        HeldFiles().Should().ContainSingle();
    }

    [Fact]
    public async Task ReplayPendingAsync_AHeldFileThatCannotBeOpened_IsLeftAndTheRestWritten()
    {
        BreakTheTrail();
        using var trail = NewTrail();
        await trail.RecordAsync(Entry("attempt"), ErasureActor.DataSubject, CancellationToken.None);
        await trail.RecordAsync(Entry("outcome"), ErasureActor.DataSubject, CancellationToken.None);
        RepairTheTrail();
        var oldest = HeldFiles()[0];

        PendingRecordsReplay pass;
        using (new FileStream(oldest, FileMode.Open, FileAccess.Read, FileShare.None)) // Another process holds it open.
            pass = await trail.ReplayPendingAsync(CancellationToken.None);

        pass.Written.Should().Be(1, "one file this pass cannot open must not hold back every record behind it");
        pass.Remaining.Should().Be(1);
        HeldFiles().Should().Equal(oldest);
        TrailPhases().Should().Equal("outcome");
    }

    [Fact]
    public async Task ReplayPendingAsync_AHeldFileNoPassCanOpen_IsCountedStuckOnceNotEveryPass()
    {
        using var outcomes = new TagCapture(AuditConventions.WriteFailures, AuditConventions.Outcome);
        BreakTheTrail();
        using var trail = NewTrail();
        await trail.RecordAsync(Entry("attempt"), ErasureActor.DataSubject, CancellationToken.None);
        RepairTheTrail();

        using (new FileStream(HeldFiles().Single(), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await trail.ReplayPendingAsync(CancellationToken.None);
            await trail.ReplayPendingAsync(CancellationToken.None);
            await trail.ReplayPendingAsync(CancellationToken.None);
        }

        outcomes.Snapshot().Count(o => o == AuditConventions.OutcomeValues.Stuck).Should().Be(1,
            "a record that cannot reach the trail must alert, but once: counted every minute it would measure time, not records");
    }

    [Fact]
    public async Task ReplayPendingAsync_ThePendingFolderRemovedThenRestoredStuck_IsReportedAgain()
    {
        using var outcomes = new TagCapture(AuditConventions.WriteFailures, AuditConventions.Outcome);
        BreakTheTrail();
        using var trail = NewTrail();
        await trail.RecordAsync(Entry("attempt"), ErasureActor.DataSubject, CancellationToken.None);
        var held = HeldFiles().Single();
        var line = File.ReadAllText(held);
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
            await trail.ReplayPendingAsync(CancellationToken.None);

        Directory.Delete(Pending, recursive: true); // Cleared by hand: the pass after finds no folder at all.
        await trail.ReplayPendingAsync(CancellationToken.None);
        Directory.CreateDirectory(Pending);
        File.WriteAllText(held, line); // Restored from a backup, still unreadable.
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
            await trail.ReplayPendingAsync(CancellationToken.None);

        outcomes.Snapshot().Count(o => o == AuditConventions.OutcomeValues.Stuck).Should().Be(2);
    }

    [Fact]
    public async Task ReplayPendingAsync_AStuckFileRemovedThenStuckAgain_IsReportedAgain()
    {
        using var outcomes = new TagCapture(AuditConventions.WriteFailures, AuditConventions.Outcome);
        BreakTheTrail();
        using var trail = NewTrail();
        await trail.RecordAsync(Entry("attempt"), ErasureActor.DataSubject, CancellationToken.None);
        var held = HeldFiles().Single();
        var line = File.ReadAllText(held);
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
            await trail.ReplayPendingAsync(CancellationToken.None);

        File.Delete(held); // Removed by hand: the pass after must forget it.
        await trail.ReplayPendingAsync(CancellationToken.None);
        File.WriteAllText(held, line);
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
            await trail.ReplayPendingAsync(CancellationToken.None);

        outcomes.Snapshot().Count(o => o == AuditConventions.OutcomeValues.Stuck).Should().Be(2,
            "a file the process forgot once it was gone is news again when it sticks again");
    }

    [Fact]
    public async Task ReplayPendingAsync_TheReplayLockCannotBeMade_SkipsAndSaysWhy()
    {
        BreakTheTrail();
        using var trail = NewTrail();
        await trail.RecordAsync(Entry("attempt"), ErasureActor.DataSubject, CancellationToken.None);
        RepairTheTrail();
        Directory.CreateDirectory(Path.Combine(Pending, ".replay.lock")); // Stands where the lock file must go.

        var pass = await trail.ReplayPendingAsync(CancellationToken.None);

        pass.Skipped.Should().BeTrue();
        pass.SkippedBecause.Should().NotBeNullOrWhiteSpace("a lock that can never be taken must be visible, not read as busy");
        pass.Remaining.Should().Be(1);
    }

    [Fact]
    public async Task RecordAsync_AHoldThatFailsAfterItsTemporaryFile_LeavesNoTemporaryFile()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var id = Guid.NewGuid();
        BreakTheTrail();
        using var trail = new JsonlComplianceAuditTrail(
            new TestConfig.StaticOptionsMonitor<AppConfig>(new AppConfig { AI = new() { Erasure = new() { AuditStoragePath = _root } } }),
            clock, _pseudonyms, NullLogger<JsonlComplianceAuditTrail>.Instance) { NewHeldId = () => id };
        // A folder takes the held file's own name, so the rename after the flush fails.
        Directory.CreateDirectory(Path.Combine(Pending, $"{clock.GetUtcNow().UtcTicks:D19}-{id:N}.pending"));

        var result = await trail.RecordAsync(Entry("attempt"), ErasureActor.DataSubject, CancellationToken.None);

        result.Errors.Single().Should().Contain("could not be held");
        Directory.GetFiles(Pending, "*.tmp").Should().BeEmpty("nothing would ever read or remove a half-held record");
    }

    [Fact]
    public async Task ReplayPendingAsync_AReplayedRecord_IsCountedUnderItsTrailNotItsContent()
    {
        using var chains = new TagCapture(AuditConventions.RecordsReplayed, AuditConventions.Chain);
        Directory.CreateDirectory(Pending);
        File.WriteAllText(Path.Combine(Pending, $"{DateTimeOffset.UtcNow.UtcTicks:D19}-planted.pending"), "{\"action\":\"made-up-42\"}");
        using var trail = NewTrail();

        await trail.ReplayPendingAsync(CancellationToken.None);

        chains.Snapshot().Should().Contain("compliance").And.NotContain("made-up-42", "a file's content must never mint a metric series");
    }

    [Fact]
    public async Task ReplayPendingAsync_ANoRecordWhoseSetAsideNameIsTaken_IsStillSetAside()
    {
        Directory.CreateDirectory(Pending);
        File.WriteAllText(Path.Combine(Pending, "0000000000000000000-torn.unreadable"), "set aside earlier");
        File.WriteAllText(Path.Combine(Pending, "0000000000000000000-torn.pending"), "{\"action\":");
        using var trail = NewTrail();

        var pass = await trail.ReplayPendingAsync(CancellationToken.None);

        pass.SetAside.Should().Be(1, "a copy of an earlier set-aside file must not keep this one in the queue for good");
        HeldFiles().Should().BeEmpty();
        File.ReadAllText(Path.Combine(Pending, "0000000000000000000-torn.unreadable")).Should().Be("set aside earlier");
    }

    [Fact]
    public async Task RecordAsync_AHeldRecord_IsCountedUnderTheComplianceTrail()
    {
        using var chains = new TagCapture(AuditConventions.WriteFailures, AuditConventions.Chain);
        BreakTheTrail();
        using var trail = NewTrail();

        await trail.RecordAsync(Entry("attempt") with { Action = "rectification" }, ErasureActor.DataSubject, CancellationToken.None);

        chains.Snapshot().Should().Contain("compliance", "every trail's failures share one counter, told apart by the trail");
    }

    [Fact]
    public async Task ReplayPendingAsync_NothingWasEverHeld_DoesNothing()
    {
        using var trail = NewTrail();

        var pass = await trail.ReplayPendingAsync(CancellationToken.None);

        pass.Should().Be(PendingRecordsReplay.Nothing);
        Directory.Exists(Pending).Should().BeFalse();
    }

    [Fact]
    public async Task RecordAsync_AHeldAndALostRecord_AreCountedByOutcome()
    {
        using var outcomes = new TagCapture(AuditConventions.WriteFailures, AuditConventions.Outcome);
        BreakTheTrail();
        using var trail = NewTrail();
        await trail.RecordAsync(Entry("attempt"), ErasureActor.DataSubject, CancellationToken.None);
        Directory.Delete(Pending, recursive: true);
        File.WriteAllText(Pending, "not a folder");

        await trail.RecordAsync(Entry("outcome"), ErasureActor.DataSubject, CancellationToken.None);

        outcomes.Snapshot().Should().Contain([AuditConventions.OutcomeValues.Pending, AuditConventions.OutcomeValues.Lost],
                "operators alert on this counter; the request it records is never refused");
    }

    /// <summary>Every value of one tag measured on one counter while it is alive; disposed so listeners never pile up.</summary>
    private sealed class TagCapture : IDisposable
    {
        private readonly List<string> _values = [];
        private readonly MeterListener _listener = new();

        public TagCapture(string instrumentName, string tagKey)
        {
            _listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == instrumentName)
                    l.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == tagKey)
                        lock (_values)
                            _values.Add((string)tag.Value!);
                }
            });
            _listener.Start();
        }

        public IReadOnlyList<string> Snapshot()
        {
            lock (_values)
                return [.. _values];
        }

        public void Dispose() => _listener.Dispose();
    }
}
