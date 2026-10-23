using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Domain.AI.Audit;
using Domain.Common.Config.AI.Audit;
using FluentAssertions;
using Infrastructure.AI.Audit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Infrastructure.AI.Tests.Audit;

/// <summary>
/// #416: the audit archiver moves finished day-files into the write-once archive only when they continue it and match
/// what it witnessed, and the application verifies its live files against the archive and the witnesses.
/// </summary>
public sealed class AuditArchiverTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _legacyFile;
    private readonly string _archiveRoot;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.Zero));
    private readonly AuditArchiver _archiver;

    public AuditArchiverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"audit-archiver-{Guid.NewGuid():N}");
        _legacyFile = Path.Combine(_tempDir, "live", "governance.jsonl");
        _archiveRoot = Path.Combine(_tempDir, "archive");
        ReadOnlyArchiveRoot.Create(_archiveRoot, "governance");
        _archiver = new AuditArchiver(
            new FileAuditArchiveStore(_archiveRoot), new AuditArchiverConfig(), _clock, NullLogger<AuditArchiver>.Instance);
    }

    public void Dispose()
    {
        ReadOnlyArchiveRoot.Unlock(_archiveRoot);
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private AuditChainLocation Chain => new("governance", DatedAuditSegments.BesideLegacyFile(_legacyFile, _clock), _legacyFile + ".lock");

    private HashChainedJsonlWriter Writer() => new(Chain, NullLogger.Instance, _archiveRoot);

    private string LiveDay(string day) => Path.Combine(_tempDir, "live", "governance", day + ".jsonl");

    private string ArchivedDay(string day) => Path.Combine(_archiveRoot, "governance", day + ".jsonl");

    /// <summary>Appends one record on each of 2026-10-19, -20 and -21, leaving the clock on the 22nd.</summary>
    private async Task ThreeDaysAsync(HashChainedJsonlWriter sut)
    {
        for (var day = 0; day < 3; day++)
        {
            (await sut.AppendAsync($"{{\"day\":{day}}}", CancellationToken.None)).IsSuccess.Should().BeTrue();
            _clock.Advance(TimeSpan.FromDays(1));
        }
    }

    /// <summary>A record line as the writer frames it, for a forger who recomputes the unkeyed hashes.</summary>
    private static (string Line, string Hash) Record(long sequence, string previousHash, string json)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Concat(sequence.ToString(CultureInfo.InvariantCulture), "\n", previousHash, "\n", json))));
        return ($"{json}\t{hash}\t{previousHash}\t{sequence}", hash);
    }

    /// <summary>Dates every archived day-file as written at <paramref name="when"/>, on the fake clock's timeline.</summary>
    private void ArchivedAt(DateTimeOffset when)
    {
        foreach (var file in Directory.GetFiles(Path.Combine(_archiveRoot, "governance"), "*.jsonl"))
            File.SetLastWriteTimeUtc(file, when.UtcDateTime);
    }

    private static async Task<List<string>> PayloadsAsync(HashChainedJsonlWriter sut)
    {
        var payloads = new List<string>();
        await foreach (var payload in sut.ReadAllPayloadsAsync(CancellationToken.None))
            payloads.Add(payload);
        return payloads;
    }

    [Fact]
    public async Task Run_ArchivesOnlyFinishedDays_AndTheApplicationVerifiesAcrossTheArchive()
    {
        using var sut = Writer();
        await ThreeDaysAsync(sut);
        (await sut.AppendAsync("{\"day\":3}", CancellationToken.None)).IsSuccess.Should().BeTrue();

        var result = await _archiver.ArchiveAsync(Chain, CancellationToken.None);

        result.Status.Should().Be(AuditArchiveStatus.Done, result.Problem);
        result.FilesArchived.Should().Be(2, "the 21st is a day old, so a host whose clock lags may still append to it");
        File.Exists(ArchivedDay("2026-10-20")).Should().BeTrue();
        File.Exists(ArchivedDay("2026-10-21")).Should().BeFalse();

        var verified = await sut.VerifyChainAsync(CancellationToken.None);
        verified.IsValid.Should().BeTrue(verified.FailureReason);
        verified.StartSequence.Should().Be(2, "the archive's earlier records were checked when they were archived");
        (await PayloadsAsync(sut)).Should().Equal("{\"day\":0}", "{\"day\":1}", "{\"day\":2}", "{\"day\":3}");
    }

    [Fact]
    public async Task Run_DeletesALiveCopyOnlyOnceItHasBeenArchivedForTheDelay()
    {
        using var sut = Writer();
        await ThreeDaysAsync(sut);
        await _archiver.ArchiveAsync(Chain, CancellationToken.None);
        ArchivedAt(_clock.GetUtcNow().AddMinutes(-5));
        (await _archiver.ArchiveAsync(Chain, CancellationToken.None)).Status.Should().Be(AuditArchiveStatus.Done);
        File.Exists(LiveDay("2026-10-19")).Should().BeTrue("archived five minutes ago, the application's mount may not list the copy yet");

        ArchivedAt(_clock.GetUtcNow().AddMinutes(-16));
        (await _archiver.ArchiveAsync(Chain, CancellationToken.None)).Status.Should().Be(AuditArchiveStatus.Done);

        File.Exists(LiveDay("2026-10-19")).Should().BeFalse();
        File.Exists(LiveDay("2026-10-20")).Should().BeFalse();
        (await sut.VerifyChainAsync(CancellationToken.None)).IsValid.Should().BeTrue();
        (await PayloadsAsync(sut)).Should().HaveCount(3);
    }

    [Fact]
    public async Task Run_StopsAtADayDeletedBeforeArchiving_AndArchivesNothingAfterIt()
    {
        using var sut = Writer();
        await ThreeDaysAsync(sut);
        (await sut.AppendAsync("{\"day\":3}", CancellationToken.None)).IsSuccess.Should().BeTrue();
        _clock.Advance(TimeSpan.FromDays(1));
        File.Delete(LiveDay("2026-10-19"));

        var result = await _archiver.ArchiveAsync(Chain, CancellationToken.None);

        result.Status.Should().Be(AuditArchiveStatus.Stopped);
        Directory.GetFiles(Path.Combine(_archiveRoot, "governance"), "*.jsonl").Should().BeEmpty(
            "a gap sealed into write-once storage could never be reported again");
    }

    [Fact]
    public async Task Witness_StopsWhenTheLiveChainWasRebuiltAfterItWasWitnessed()
    {
        using var sut = Writer();
        await ThreeDaysAsync(sut);
        (await _archiver.WitnessAsync(Chain, CancellationToken.None)).Status.Should().Be(AuditArchiveStatus.Done);

        await RebuildLiveChainAsync();

        var result = await _archiver.WitnessAsync(Chain, CancellationToken.None);
        result.Status.Should().Be(AuditArchiveStatus.Stopped);
        result.Problem.Should().Contain("witnessed");
    }

    [Fact]
    public async Task Verification_BreaksOnARebuiltChain_EvenWhenItsOwnCheckpointWasRewrittenToMatch()
    {
        using var sut = Writer();
        await ThreeDaysAsync(sut);
        await _archiver.WitnessAsync(Chain, CancellationToken.None);

        var forgedHead = await RebuildLiveChainAsync();

        var verified = await sut.VerifyChainAsync(new AuditChainCheckpoint(2, forgedHead), CancellationToken.None);
        verified.IsValid.Should().BeFalse("the archiver's witness is a checkpoint this host cannot rewrite");
        verified.FailureReason.Should().Contain("differs from the one last verified");
    }

    [Fact]
    public async Task Witness_StopsWhenRecordsWereCutFromTheEnd()
    {
        using var sut = Writer();
        await ThreeDaysAsync(sut);
        await _archiver.WitnessAsync(Chain, CancellationToken.None);

        File.Delete(LiveDay("2026-10-21"));

        var result = await _archiver.WitnessAsync(Chain, CancellationToken.None);
        result.Status.Should().Be(AuditArchiveStatus.Stopped);
        result.Problem.Should().Contain("before the witnessed sequence 2");
    }

    [Fact]
    public async Task Witness_LeavesOutAnAppendInProgressInTheNewestFile()
    {
        using var sut = Writer();
        await ThreeDaysAsync(sut);
        await File.AppendAllTextAsync(LiveDay("2026-10-21"), "{\"half\":");

        (await _archiver.WitnessAsync(Chain, CancellationToken.None)).Status.Should().Be(AuditArchiveStatus.Done);
        Directory.GetFiles(Path.Combine(_archiveRoot, "governance", "witnesses")).Select(Path.GetFileName)
            .Should().Equal(AuditWitness.FileName(2));
    }

    [Fact]
    public async Task Run_RefusesAFileTheApplicationWouldReadDifferently()
    {
        // A carriage return splits a line for the application's reader but not for a reader that splits on \n alone,
        // so archiving it would seal a break the application reports for ever. The writer never writes one.
        using var sut = Writer();
        var first = Record(0, HashChainedJsonlWriter.GenesisHash, "{\"a\":\"x\ry\"}");
        Directory.CreateDirectory(Path.GetDirectoryName(LiveDay("2026-10-19"))!);
        await File.WriteAllTextAsync(LiveDay("2026-10-19"), first.Line + "\n");
        await File.WriteAllTextAsync(LiveDay("2026-10-20"), Record(1, first.Hash, "{\"b\":1}").Line + "\n");
        await File.WriteAllTextAsync(_legacyFile + ".lock", string.Empty);
        _clock.Advance(TimeSpan.FromDays(3));

        var result = await _archiver.ArchiveAsync(Chain, CancellationToken.None);

        result.Status.Should().Be(AuditArchiveStatus.Stopped);
        result.Problem.Should().Contain("carriage return");
        File.Exists(ArchivedDay("2026-10-19")).Should().BeFalse();
    }

    [Fact]
    public async Task Run_StopsAtADayTornByACrash_UntilAnOperatorAcceptsExactlyThoseBytes()
    {
        using var sut = Writer();
        (await sut.AppendAsync("{\"day\":0}", CancellationToken.None)).IsSuccess.Should().BeTrue();
        _clock.Advance(TimeSpan.FromDays(1));
        (await sut.AppendAsync("{\"day\":1}", CancellationToken.None)).IsSuccess.Should().BeTrue();
        await File.AppendAllTextAsync(LiveDay("2026-10-20"), "{\"torn\":");
        _clock.Advance(TimeSpan.FromDays(1));
        (await sut.AppendAsync("{\"day\":2}", CancellationToken.None)).IsSuccess.Should().BeTrue();
        _clock.Advance(TimeSpan.FromDays(1));

        (await _archiver.ArchiveAsync(Chain, CancellationToken.None)).Status.Should().Be(AuditArchiveStatus.Stopped);

        var sha = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(LiveDay("2026-10-20"))));
        (await _archiver.AcceptAsync(Chain, "2026-10-20.jsonl", sha, 1, "crash mid-write", CancellationToken.None))
            .Status.Should().Be(AuditArchiveStatus.Done);
        var result = await _archiver.ArchiveAsync(Chain, CancellationToken.None);

        result.Status.Should().Be(AuditArchiveStatus.Done, result.Problem);
        result.FilesArchived.Should().Be(2);
        (await sut.VerifyChainAsync(CancellationToken.None)).IsValid.Should().BeTrue("the live chain continues the accepted file's last record");

        // The application's checkpoint stayed before the tear (a broken pass never moves it), so its next pass walks
        // back into the archive and through the accepted file: the torn line must be stepped over there too.
        var dayZero = (await File.ReadAllLinesAsync(ArchivedDay("2026-10-19"))).Single().Split('\t');
        var verified = await sut.VerifyChainAsync(new AuditChainCheckpoint(0, dayZero[1]), CancellationToken.None);
        verified.IsValid.Should().BeTrue(verified.FailureReason);
        verified.StartSequence.Should().Be(0);
    }

    [Fact]
    public async Task Accept_RefusesBytesThatChangedSinceTheyWereInspected()
    {
        using var sut = Writer();
        await ThreeDaysAsync(sut);
        var sha = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(LiveDay("2026-10-20"))));
        await File.AppendAllTextAsync(LiveDay("2026-10-20"), "{\"planted\":");

        var result = await _archiver.AcceptAsync(Chain, "2026-10-20.jsonl", sha, 1, "crash", CancellationToken.None);

        result.Status.Should().Be(AuditArchiveStatus.Stopped);
        Directory.Exists(Path.Combine(_archiveRoot, "governance", "accepted")).Should().BeFalse();
    }

    [Fact]
    public async Task Run_DeletesAnEmptyDayLeftByACrash_InsteadOfArchivingIt()
    {
        using var sut = Writer();
        (await sut.AppendAsync("{\"day\":0}", CancellationToken.None)).IsSuccess.Should().BeTrue();
        await File.WriteAllTextAsync(LiveDay("2026-10-20"), string.Empty);
        _clock.Advance(TimeSpan.FromDays(2));
        (await sut.AppendAsync("{\"day\":2}", CancellationToken.None)).IsSuccess.Should().BeTrue();
        _clock.Advance(TimeSpan.FromDays(1));

        var result = await _archiver.ArchiveAsync(Chain, CancellationToken.None);

        result.Status.Should().Be(AuditArchiveStatus.Done, result.Problem);
        File.Exists(LiveDay("2026-10-20")).Should().BeFalse();
        File.Exists(ArchivedDay("2026-10-20")).Should().BeFalse();
        (await sut.VerifyChainAsync(CancellationToken.None)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Run_IgnoresAnAcceptanceOnceTheFileHasChangedSince()
    {
        using var sut = Writer();
        (await sut.AppendAsync("{\"day\":0}", CancellationToken.None)).IsSuccess.Should().BeTrue();
        _clock.Advance(TimeSpan.FromDays(1));
        (await sut.AppendAsync("{\"day\":1}", CancellationToken.None)).IsSuccess.Should().BeTrue();
        await File.AppendAllTextAsync(LiveDay("2026-10-20"), "{\"torn\":");
        var sha = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(LiveDay("2026-10-20"))));
        (await _archiver.AcceptAsync(Chain, "2026-10-20.jsonl", sha, 1, "crash", CancellationToken.None)).Status.Should().Be(AuditArchiveStatus.Done);
        await File.AppendAllTextAsync(LiveDay("2026-10-20"), "\n{\"slipped in\":true}\n");
        _clock.Advance(TimeSpan.FromDays(1));
        (await sut.AppendAsync("{\"day\":2}", CancellationToken.None)).IsSuccess.Should().BeTrue();
        _clock.Advance(TimeSpan.FromDays(1));

        var result = await _archiver.ArchiveAsync(Chain, CancellationToken.None);

        result.Status.Should().Be(AuditArchiveStatus.Stopped);
        File.Exists(ArchivedDay("2026-10-20")).Should().BeFalse("the acceptance covers only the bytes that were inspected");
    }

    [Fact]
    public async Task Run_StopsWhenALiveCopyOfAnArchivedFileWasChanged()
    {
        using var sut = Writer();
        await ThreeDaysAsync(sut);
        await _archiver.ArchiveAsync(Chain, CancellationToken.None);
        await File.AppendAllTextAsync(LiveDay("2026-10-19"), "{\"planted\":1}\n");

        var result = await _archiver.ArchiveAsync(Chain, CancellationToken.None);

        result.Status.Should().Be(AuditArchiveStatus.Stopped);
        result.Problem.Should().Contain("differs from its archived copy");
        File.Exists(LiveDay("2026-10-19")).Should().BeTrue();
    }

    [Fact]
    public async Task Run_StopsAtALiveFileSortingWithinTheArchiveThatItNeverHeld()
    {
        using var sut = Writer();
        await ThreeDaysAsync(sut);
        await _archiver.ArchiveAsync(Chain, CancellationToken.None);
        await File.WriteAllTextAsync(LiveDay("2026-10-18"), "{\"planted\":1}\n");

        var result = await _archiver.ArchiveAsync(Chain, CancellationToken.None);

        result.Status.Should().Be(AuditArchiveStatus.Stopped);
        result.Problem.Should().Contain("has no copy");
    }

    [Fact]
    public async Task Run_RefusesAFileLargerThanTheLimit()
    {
        using var sut = Writer();
        await ThreeDaysAsync(sut);
        var tiny = new AuditArchiver(
            new FileAuditArchiveStore(_archiveRoot), new AuditArchiverConfig { MaxFileMegabytes = 0 }, _clock,
            NullLogger<AuditArchiver>.Instance);

        var result = await tiny.ArchiveAsync(Chain, CancellationToken.None);

        result.Status.Should().Be(AuditArchiveStatus.Stopped);
        result.Problem.Should().Contain("larger than");
    }

    [Fact]
    public async Task LinesPlantedBeforeTheFirstRecordOfADayFile_StopTheArchiver_AndBreakVerification()
    {
        // Only the earlier single file may start with lines written before hash chaining: anywhere else they are
        // records no hash or witness covers, which readers would still serve.
        using var sut = Writer();
        await ThreeDaysAsync(sut);
        var day = await File.ReadAllTextAsync(LiveDay("2026-10-19"));
        await File.WriteAllTextAsync(LiveDay("2026-10-19"), "{\"decision\":\"allow\"}\n" + day);

        (await _archiver.ArchiveAsync(Chain, CancellationToken.None)).Status.Should().Be(AuditArchiveStatus.Stopped);
        File.Exists(ArchivedDay("2026-10-19")).Should().BeFalse();
        (await sut.VerifyChainAsync(CancellationToken.None)).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task AnAcceptedFile_CannotCarryAWholeRecordThatDoesNotChain()
    {
        // A record that hashes right but does not chain would become the archive's newest record and outrank every
        // witness. Accepting a file lets through only what a crash leaves.
        using var sut = Writer();
        (await sut.AppendAsync("{\"day\":0}", CancellationToken.None)).IsSuccess.Should().BeTrue();
        _clock.Advance(TimeSpan.FromDays(1));
        (await sut.AppendAsync("{\"day\":1}", CancellationToken.None)).IsSuccess.Should().BeTrue();
        await File.AppendAllTextAsync(LiveDay("2026-10-20"), "{\"torn\":\n");
        _clock.Advance(TimeSpan.FromDays(1));
        (await sut.AppendAsync("{\"day\":2}", CancellationToken.None)).IsSuccess.Should().BeTrue();
        _clock.Advance(TimeSpan.FromDays(1));
        // Planted after the writers moved on, so the live chain itself still continues: only the acceptance rule stops it.
        var planted = Record(9_000_000_000_000, new string('e', 64), "{\"planted\":true}");
        await File.AppendAllTextAsync(LiveDay("2026-10-20"), planted.Line + "\n");
        var sha = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(LiveDay("2026-10-20"))));
        await _archiver.AcceptAsync(Chain, "2026-10-20.jsonl", sha, 1, "crash", CancellationToken.None);

        var result = await _archiver.ArchiveAsync(Chain, CancellationToken.None);

        result.Status.Should().Be(AuditArchiveStatus.Stopped);
        File.Exists(ArchivedDay("2026-10-20")).Should().BeFalse();
    }

    [Fact]
    public async Task Verify_AfterTheOldestArchivedDayExpires_StillPasses_WithItsWitnessesAndMarkerKept()
    {
        // #901: the archive's retention is the storage's own: once the locked period passes, a deletion rule removes the
        // oldest day-files. Verification must then start at the oldest remaining record, not report the chain broken.
        using var sut = Writer();
        await ThreeDaysAsync(sut);
        (await _archiver.ArchiveAsync(Chain, CancellationToken.None)).Status.Should().Be(AuditArchiveStatus.Done);
        (await _archiver.WitnessAsync(Chain, CancellationToken.None)).Status.Should().Be(AuditArchiveStatus.Done);
        (await _archiver.VerifyAsync(Chain, CancellationToken.None)).Status.Should().Be(AuditArchiveStatus.Done, "control");

        // Long before a retention period ends, the archiver has deleted the live copies of the days it archived.
        _clock.Advance(TimeSpan.FromHours(1));
        ArchivedAt(_clock.GetUtcNow() - TimeSpan.FromHours(1));
        (await _archiver.ArchiveAsync(Chain, CancellationToken.None)).Status.Should().Be(AuditArchiveStatus.Done);
        File.Exists(LiveDay("2026-10-19")).Should().BeFalse("control: the live copy of an archived day is gone");

        ReadOnlyArchiveRoot.Unlock(_archiveRoot);
        File.Delete(ArchivedDay("2026-10-19"));
        ReadOnlyArchiveRoot.Lock(_archiveRoot);

        var result = await _archiver.VerifyAsync(Chain, CancellationToken.None);
        result.Status.Should().Be(AuditArchiveStatus.Done, result.Problem);
        var verified = await sut.VerifyChainAsync(CancellationToken.None);
        verified.IsValid.Should().BeTrue(verified.FailureReason);
    }

    [Fact]
    public async Task Verify_ChecksArchivedFilesAgainstWitnessesTheyHaveAlreadyPassed()
    {
        using var sut = Writer();
        await ThreeDaysAsync(sut);
        await _archiver.ArchiveAsync(Chain, CancellationToken.None);
        // A witness at sequence 0 the archived 19th contradicts, as if the archived copy were not what was witnessed.
        var witnesses = Path.Combine(_archiveRoot, "governance", "witnesses");
        Directory.CreateDirectory(witnesses);
        await File.WriteAllBytesAsync(Path.Combine(witnesses, AuditWitness.FileName(0)),
            new AuditWitness(0, new string('f', 64), "2026-10-19.jsonl", _clock.GetUtcNow()).ToBytes());

        var result = await _archiver.VerifyAsync(Chain, CancellationToken.None);

        result.Status.Should().Be(AuditArchiveStatus.Stopped);
        result.Problem.Should().Contain("witnessed");
    }

    [Fact]
    public async Task Witness_PinsTheRecordsBeforeABreak_SoABreakCannotHoldTheWitnessBack()
    {
        using var sut = Writer();
        await ThreeDaysAsync(sut);
        (await _archiver.WitnessAsync(Chain, CancellationToken.None)).Status.Should().Be(AuditArchiveStatus.Done);
        (await sut.AppendAsync("{\"n\":3}", CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await sut.AppendAsync("{\"n\":4}", CancellationToken.None)).IsSuccess.Should().BeTrue();
        await File.AppendAllTextAsync(LiveDay("2026-10-22"), "{\"planted\":\n");
        (await sut.AppendAsync("{\"n\":5}", CancellationToken.None)).IsSuccess.Should().BeTrue();

        var result = await _archiver.WitnessAsync(Chain, CancellationToken.None);

        result.Status.Should().Be(AuditArchiveStatus.Stopped);
        Directory.GetFiles(Path.Combine(_archiveRoot, "governance", "witnesses")).Select(Path.GetFileName)
            .Should().Contain(AuditWitness.FileName(4), "records 3 and 4 verified before the break");
    }

    [Fact]
    public async Task Run_StopsArchivingAtAnEmptyDayItCouldNotDelete()
    {
        // Archiving the days after it would leave it sorting within the archive with no copy: a break for ever.
        using var sut = Writer();
        (await sut.AppendAsync("{\"day\":0}", CancellationToken.None)).IsSuccess.Should().BeTrue();
        await File.WriteAllTextAsync(LiveDay("2026-10-20"), string.Empty);
        _clock.Advance(TimeSpan.FromDays(2));
        (await sut.AppendAsync("{\"day\":2}", CancellationToken.None)).IsSuccess.Should().BeTrue();
        _clock.Advance(TimeSpan.FromDays(1));
        (await sut.AppendAsync("{\"day\":3}", CancellationToken.None)).IsSuccess.Should().BeTrue();
        _clock.Advance(TimeSpan.FromDays(2));
        File.Delete(_legacyFile + ".lock");

        var result = await _archiver.ArchiveAsync(Chain, CancellationToken.None);

        result.Status.Should().Be(AuditArchiveStatus.Done, result.Problem);
        File.Exists(LiveDay("2026-10-20")).Should().BeTrue("without the lock it is not deleted");
        File.Exists(ArchivedDay("2026-10-21")).Should().BeFalse();
    }

    [Fact]
    public async Task AStoreThatFails_StopsTheLog_RatherThanEndingTheRun()
    {
        using var sut = Writer();
        await ThreeDaysAsync(sut);
        var failing = new AuditArchiver(
            new FailingWrites(new FileAuditArchiveStore(_archiveRoot)), new AuditArchiverConfig(), _clock,
            NullLogger<AuditArchiver>.Instance);

        var result = await failing.ArchiveAsync(Chain, CancellationToken.None);

        result.Status.Should().Be(AuditArchiveStatus.Stopped);
        result.Problem.Should().Contain("storage refused");
    }

    [Fact]
    public async Task Verify_StopsAtALiveFileSortingWithinTheArchiveThatItNeverHeld()
    {
        using var sut = Writer();
        await ThreeDaysAsync(sut);
        await _archiver.ArchiveAsync(Chain, CancellationToken.None);
        await File.WriteAllTextAsync(LiveDay("2026-10-18"), "{\"planted\":1}\n");

        (await _archiver.VerifyAsync(Chain, CancellationToken.None)).Problem.Should().Contain("has no copy");
    }

    [Fact]
    public async Task AnOlderSingleFileWithWindowsLineEnds_IsArchivedOnceAccepted_AndStillVerifies()
    {
        // Lines from before hash chaining may have been written with \r\n; the strict reader refuses them, and an
        // acceptance reads the file exactly as the application does.
        Directory.CreateDirectory(Path.GetDirectoryName(_legacyFile)!);
        await File.WriteAllTextAsync(_legacyFile, "{\"before\":\"chaining\"}\r\n");
        using var sut = Writer();
        await ThreeDaysAsync(sut);

        (await _archiver.ArchiveAsync(Chain, CancellationToken.None)).Status.Should().Be(AuditArchiveStatus.Stopped);
        var sha = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(_legacyFile)));
        // 0: its one line comes before any record, so it is history pinned by the hash, not a line to step over.
        (await _archiver.AcceptAsync(Chain, "governance.jsonl", sha, 0, "written before chaining", CancellationToken.None))
            .Status.Should().Be(AuditArchiveStatus.Done);

        // The archiver, with its own credential, may write the archive's root; the application, below, may not.
        ReadOnlyArchiveRoot.Unlock(_archiveRoot);
        var result = await _archiver.ArchiveAsync(Chain, CancellationToken.None);
        ReadOnlyArchiveRoot.Lock(_archiveRoot);

        result.Status.Should().Be(AuditArchiveStatus.Done, result.Problem);
        File.Exists(Path.Combine(_archiveRoot, "governance.jsonl")).Should().BeTrue();
        var verified = await sut.VerifyChainAsync(CancellationToken.None);
        verified.IsValid.Should().BeTrue(verified.FailureReason);
    }

    [Fact]
    public async Task AnAcceptedSingleFileAsTheArchivesNewest_KeepsEveryLaterStepWorking()
    {
        // Archived alone (its day-files are still young), it is the archive's newest file on every later run, and must
        // be read the way it was accepted, not strictly, or the log would stop for good.
        Directory.CreateDirectory(Path.GetDirectoryName(_legacyFile)!);
        await File.WriteAllTextAsync(_legacyFile, "{\"before\":\"chaining\"}\r\n");
        using var sut = Writer();
        (await sut.AppendAsync("{\"day\":0}", CancellationToken.None)).IsSuccess.Should().BeTrue();
        _clock.Advance(TimeSpan.FromDays(1));
        (await sut.AppendAsync("{\"day\":1}", CancellationToken.None)).IsSuccess.Should().BeTrue();
        var sha = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(_legacyFile)));
        (await _archiver.AcceptAsync(Chain, "governance.jsonl", sha, 0, "written before chaining", CancellationToken.None))
            .Status.Should().Be(AuditArchiveStatus.Done);

        ReadOnlyArchiveRoot.Unlock(_archiveRoot);
        (await _archiver.ArchiveAsync(Chain, CancellationToken.None)).FilesArchived.Should().Be(1);
        var again = await _archiver.ArchiveAsync(Chain, CancellationToken.None);
        var witness = await _archiver.WitnessAsync(Chain, CancellationToken.None);
        var verify = await _archiver.VerifyAsync(Chain, CancellationToken.None);
        ReadOnlyArchiveRoot.Lock(_archiveRoot);

        again.Status.Should().Be(AuditArchiveStatus.Done, again.Problem);
        witness.Status.Should().Be(AuditArchiveStatus.Done, witness.Problem);
        verify.Status.Should().Be(AuditArchiveStatus.Done, verify.Problem);
    }

    [Fact]
    public async Task ADriftLogWithDayFilesFromBeforeChaining_IsBrokenUntilAccepted_ThenArchivedAndVerified()
    {
        // The drift log was split by day from the start and has no earlier single file: its oldest day-files hold
        // lines from before hash chaining. No day-file may hold such lines unless an operator accepts its exact bytes:
        // anything looser would let the application's identity add such lines to a drift log that is still idle.
        var folder = Path.Combine(_tempDir, "live", "drift-audit");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "2026-10-17.jsonl"), "{\"unchained\":1}\n");
        var drift = new AuditChainLocation("drift", new DatedAuditSegments(folder, legacyFile: null, _clock), Path.Combine(folder, ".chain.lock"));
        ReadOnlyArchiveRoot.Unlock(_archiveRoot);
        Directory.CreateDirectory(Path.Combine(_archiveRoot, "drift-audit"));
        ReadOnlyArchiveRoot.Lock(_archiveRoot);
        using var sut = new HashChainedJsonlWriter(drift, NullLogger.Instance, _archiveRoot);
        await ThreeDaysAsync(sut);

        (await sut.VerifyChainAsync(CancellationToken.None)).IsValid.Should().BeFalse("an unaccepted day-file holds lines no hash covers");
        (await _archiver.ArchiveAsync(drift, CancellationToken.None)).Status.Should().Be(AuditArchiveStatus.Stopped);

        var oldFile = Path.Combine(folder, "2026-10-17.jsonl");
        var sha = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(oldFile)));
        (await _archiver.AcceptAsync(drift, "2026-10-17.jsonl", sha, 0, "drift lines from before chaining", CancellationToken.None))
            .Status.Should().Be(AuditArchiveStatus.Done);
        var result = await _archiver.ArchiveAsync(drift, CancellationToken.None);

        result.Status.Should().Be(AuditArchiveStatus.Done, result.Problem);
        File.Exists(Path.Combine(_archiveRoot, "drift-audit", "2026-10-17.jsonl")).Should().BeTrue();
        var verified = await sut.VerifyChainAsync(CancellationToken.None);
        verified.IsValid.Should().BeTrue(verified.FailureReason);
        (await _archiver.VerifyAsync(drift, CancellationToken.None)).Status.Should().Be(AuditArchiveStatus.Done);
    }

    [Fact]
    public async Task Accept_RefusesWhenTheOperatorCountedADifferentNumberOfLinesToStepOver()
    {
        // A line placed beside a torn one would otherwise slip through with it.
        using var sut = Writer();
        await ThreeDaysAsync(sut);
        await File.AppendAllTextAsync(LiveDay("2026-10-20"), "{\"torn\":\n{\"decision\":\"allow\"}\n");
        var sha = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(LiveDay("2026-10-20"))));

        var result = await _archiver.AcceptAsync(Chain, "2026-10-20.jsonl", sha, 1, "crash", CancellationToken.None);

        result.Status.Should().Be(AuditArchiveStatus.Stopped);
        result.Problem.Should().Contain("line 2, 3");
    }

    [Fact]
    public async Task Readers_SkipALineThatIsNotARecordOnceTheRecordsHaveBegun()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_legacyFile)!);
        await File.WriteAllTextAsync(_legacyFile, "{\"before\":\"chaining\"}\n");
        using var sut = Writer();
        await ThreeDaysAsync(sut);
        await File.AppendAllTextAsync(LiveDay("2026-10-20"), "{\"decision\":\"allow\"}\n");
        // Framed like a record, but its hash is not its own: no more a record than the plain line.
        await File.AppendAllTextAsync(LiveDay("2026-10-20"),
            $"{{\"decision\":\"deny\"}}\t{new string('a', 64)}\t{new string('b', 64)}\t7\n");

        (await PayloadsAsync(sut)).Should().Equal(
            "{\"before\":\"chaining\"}", "{\"day\":0}", "{\"day\":1}", "{\"day\":2}");
    }

    [Fact]
    public async Task Readers_ReturnNothingFromAFileTheyWereToldToLeaveOut_EvenBeforeTheRecordsBegin()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_legacyFile)!);
        await File.WriteAllTextAsync(_legacyFile, "{\"before\":\"chaining\"}\n");
        using var sut = Writer();
        await ThreeDaysAsync(sut);

        var payloads = new List<string>();
        await foreach (var payload in sut.ReadPayloadsAsync(f => DatedAuditSegments.DayOf(f) is not null, CancellationToken.None))
            payloads.Add(payload);

        payloads.Should().Equal("{\"day\":0}", "{\"day\":1}", "{\"day\":2}");
    }

    /// <summary>A store whose writes fail the way an unreachable or unauthorised Blob container's do.</summary>
    private sealed class FailingWrites(Application.AI.Common.Interfaces.Audit.IAuditArchiveStore inner)
        : Application.AI.Common.Interfaces.Audit.IAuditArchiveStore
    {
        public Task<IReadOnlyList<AuditArchiveItem>> ListAsync(string folder, CancellationToken cancellationToken) =>
            inner.ListAsync(folder, cancellationToken);

        public Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken) => inner.ReadAsync(path, cancellationToken);

        public Task<AuditArchivePut> PutIfAbsentAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("storage refused the request");
    }

    [Fact]
    public async Task Run_WithoutTheArchiveMarker_IsNotReady_AndTouchesNothing()
    {
        using var sut = Writer();
        await ThreeDaysAsync(sut);
        var unmounted = new AuditArchiver(
            new FileAuditArchiveStore(Path.Combine(_tempDir, "not-mounted")), new AuditArchiverConfig(), _clock,
            NullLogger<AuditArchiver>.Instance);

        (await unmounted.ArchiveAsync(Chain, CancellationToken.None)).Status.Should().Be(AuditArchiveStatus.NotReady);
        Directory.Exists(Path.Combine(_tempDir, "not-mounted")).Should().BeFalse();
    }

    [Fact]
    public async Task Verify_ReportsAnArchivedFileChangedAfterArchiving()
    {
        using var sut = Writer();
        await ThreeDaysAsync(sut);
        await _archiver.ArchiveAsync(Chain, CancellationToken.None);
        (await _archiver.VerifyAsync(Chain, CancellationToken.None)).Status.Should().Be(AuditArchiveStatus.Done);

        var lines = await File.ReadAllLinesAsync(ArchivedDay("2026-10-19"));
        await File.WriteAllTextAsync(ArchivedDay("2026-10-19"), lines[0].Replace("\"day\":0", "\"day\":9") + "\n");

        (await _archiver.VerifyAsync(Chain, CancellationToken.None)).Status.Should().Be(AuditArchiveStatus.Stopped);
    }

    /// <summary>
    /// What someone in control of the live folder can do, the hashes being unkeyed: replace every live record with a
    /// self-consistent chain of their own. Returns the forged head's hash.
    /// </summary>
    private async Task<string> RebuildLiveChainAsync()
    {
        var previous = HashChainedJsonlWriter.GenesisHash;
        string[] days = ["2026-10-19", "2026-10-20", "2026-10-21"];
        for (var i = 0; i < days.Length; i++)
        {
            var record = Record(i, previous, $"{{\"forged\":{i}}}");
            await File.WriteAllTextAsync(LiveDay(days[i]), record.Line + "\n");
            previous = record.Hash;
        }

        File.Delete(_legacyFile + ".lock");
        await File.WriteAllTextAsync(_legacyFile + ".lock", string.Empty);
        return previous;
    }
}
