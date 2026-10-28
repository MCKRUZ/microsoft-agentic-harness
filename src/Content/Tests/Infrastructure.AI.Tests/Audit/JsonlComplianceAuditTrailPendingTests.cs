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
        result.Errors.Single().Should().Contain(Pending);
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
        var outcomes = CaptureFailureOutcomes();
        BreakTheTrail();
        using var trail = NewTrail();
        await trail.RecordAsync(Entry("attempt"), ErasureActor.DataSubject, CancellationToken.None);
        Directory.Delete(Pending, recursive: true);
        File.WriteAllText(Pending, "not a folder");

        await trail.RecordAsync(Entry("outcome"), ErasureActor.DataSubject, CancellationToken.None);

        lock (outcomes)
            outcomes.Should().Contain([ComplianceConventions.OutcomeValues.Pending, ComplianceConventions.OutcomeValues.Lost],
                "operators alert on this counter; the request it records is never refused");
    }

    private static List<string> CaptureFailureOutcomes()
    {
        var outcomes = new List<string>();
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == ComplianceConventions.AuditWriteFailures)
                    l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == ComplianceConventions.Outcome)
                    lock (outcomes)
                        outcomes.Add((string)tag.Value!);
            }
        });
        listener.Start();
        return outcomes;
    }
}
