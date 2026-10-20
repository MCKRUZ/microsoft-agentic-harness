using Domain.AI.Audit;
using FluentAssertions;
using Infrastructure.AI.Audit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Infrastructure.AI.Tests.Audit;

/// <summary>
/// #416: the storage platform, not the application, moves a chain's finished day-files into a read-only archive.
/// The live files must continue the archive's newest record exactly; anything else is a break or, when the archive
/// is not there at all, "not verified".
/// </summary>
public sealed class ArchivedAuditChainTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _legacyFile;
    private readonly string _archiveRoot;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.Zero));

    public ArchivedAuditChainTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"archived-audit-{Guid.NewGuid():N}");
        _legacyFile = Path.Combine(_tempDir, "live", "governance.jsonl");
        _archiveRoot = Path.Combine(_tempDir, "archive");
        ReadOnlyArchiveRoot.Create(_archiveRoot, "governance");
    }

    public void Dispose()
    {
        ReadOnlyArchiveRoot.Unlock(_archiveRoot);
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private HashChainedJsonlWriter Writer(string? archiveRoot = null) => new(
        DatedAuditSegments.BesideLegacyFile(_legacyFile, _clock), _legacyFile + ".lock", NullLogger.Instance,
        archiveRoot ?? _archiveRoot);

    private string LiveDay(string day) => Path.Combine(_tempDir, "live", "governance", day + ".jsonl");

    private string ArchivedDay(string day) => Path.Combine(_archiveRoot, "governance", day + ".jsonl");

    /// <summary>What the platform job does: copy a finished file into the archive, then delete the live one.</summary>
    private void Archive(string day, bool deleteLive = true)
    {
        Directory.CreateDirectory(Path.Combine(_archiveRoot, "governance"));
        File.Copy(LiveDay(day), ArchivedDay(day));
        if (deleteLive)
            File.Delete(LiveDay(day));
    }

    /// <summary>Appends one record on each of three days: 2026-10-19, -20 and -21.</summary>
    private async Task ThreeDaysAsync(HashChainedJsonlWriter sut)
    {
        for (var day = 0; day < 3; day++)
        {
            (await sut.AppendAsync($"{{\"day\":{day}}}", CancellationToken.None)).IsSuccess.Should().BeTrue();
            _clock.Advance(TimeSpan.FromDays(1));
        }
    }

    private static async Task<List<string>> PayloadsAsync(HashChainedJsonlWriter sut)
    {
        var payloads = new List<string>();
        await foreach (var payload in sut.ReadAllPayloadsAsync(CancellationToken.None))
            payloads.Add(payload);
        return payloads;
    }

    [Fact]
    public async Task ArchivedFrontDays_VerifyWithTheLiveDaysAsOneChain_AndReadersSeeEveryRecord()
    {
        using var sut = Writer();
        await ThreeDaysAsync(sut);
        Archive("2026-10-19");
        Archive("2026-10-20");

        var verified = await sut.VerifyChainAsync(CancellationToken.None);

        verified.IsValid.Should().BeTrue(verified.FailureReason);
        verified.HeadSequence.Should().Be(2);
        (await PayloadsAsync(sut)).Should().Equal("{\"day\":0}", "{\"day\":1}", "{\"day\":2}");
    }

    [Fact]
    public async Task LiveRecordsDeletedBeforeTheyWereArchived_AreABreak()
    {
        using var sut = Writer();
        await ThreeDaysAsync(sut);
        Archive("2026-10-19");
        File.Delete(LiveDay("2026-10-20")); // never archived

        var verified = await sut.VerifyChainAsync(CancellationToken.None);

        verified.IsValid.Should().BeFalse();
        verified.FailureReason.Should().Contain("Sequence gap");
    }

    [Fact]
    public async Task AFileCopiedButNotYetDeleted_IsReadOnce()
    {
        using var sut = Writer();
        await ThreeDaysAsync(sut);
        Archive("2026-10-19", deleteLive: false);

        (await sut.VerifyChainAsync(CancellationToken.None)).IsValid.Should().BeTrue();
        (await PayloadsAsync(sut)).Should().HaveCount(3);
    }

    [Fact]
    public async Task AnArchivedCopyThatDiffersFromTheLiveFile_IsABreak()
    {
        using var sut = Writer();
        await ThreeDaysAsync(sut);
        Archive("2026-10-19", deleteLive: false);
        await File.AppendAllTextAsync(ArchivedDay("2026-10-19"), "{\"extra\":true}\n");

        var verified = await sut.VerifyChainAsync(CancellationToken.None);

        verified.IsValid.Should().BeFalse();
        verified.FailureReason.Should().Contain("differs from its archived copy");
    }

    [Fact]
    public async Task AnArchivedFileThatDoesNotEndWithAWholeRecord_IsABreak()
    {
        using var sut = Writer();
        await ThreeDaysAsync(sut);
        Archive("2026-10-19");
        await File.AppendAllTextAsync(ArchivedDay("2026-10-19"), "{\"torn\":");

        var verified = await sut.VerifyChainAsync(CancellationToken.None);

        verified.IsValid.Should().BeFalse();
        verified.FailureReason.Should().Contain("does not end with a whole record");
    }

    [Fact]
    public async Task AnArchiveThatIsNotThere_IsNotVerified_AndRecordsAreNotWritten()
    {
        // An unmounted share must not make every archived record read as deleted.
        using var sut = Writer(Path.Combine(_tempDir, "not-mounted"));

        (await sut.AppendAsync("{\"n\":0}", CancellationToken.None)).IsSuccess.Should().BeFalse();
        var verify = () => sut.VerifyChainAsync(CancellationToken.None);
        await verify.Should().ThrowAsync<AuditFolderRefusedException>();
    }

    [Fact]
    public async Task AWriterThatLostItsHint_ContinuesFromTheArchive()
    {
        using var sut = Writer();
        await ThreeDaysAsync(sut);
        Archive("2026-10-19");
        Archive("2026-10-20");
        File.Delete(_legacyFile + ".lock");

        (await sut.AppendAsync("{\"day\":3}", CancellationToken.None)).IsSuccess.Should().BeTrue();

        var verified = await sut.VerifyChainAsync(CancellationToken.None);
        verified.IsValid.Should().BeTrue(verified.FailureReason);
        verified.HeadSequence.Should().Be(3);
    }

    [Fact]
    public async Task AWriterThatLostItsHint_RefusesToAppendOntoAGapAfterTheArchive()
    {
        using var sut = Writer();
        await ThreeDaysAsync(sut);
        Archive("2026-10-19");
        File.Delete(LiveDay("2026-10-20")); // the live chain no longer continues the archive
        File.Delete(_legacyFile + ".lock");

        (await sut.AppendAsync("{\"day\":3}", CancellationToken.None)).IsSuccess.Should().BeFalse();
        (await File.ReadAllLinesAsync(LiveDay("2026-10-21"))).Should().ContainSingle("nothing was chained onto the gap");
    }

    [Fact]
    public async Task ACheckpointAtTheArchivesNewestRecord_IsStillCompared()
    {
        using var sut = Writer();
        await ThreeDaysAsync(sut);
        Archive("2026-10-19");
        var archivedHead = (await File.ReadAllLinesAsync(ArchivedDay("2026-10-19"))).Single().Split('\t');

        (await sut.VerifyChainAsync(new AuditChainCheckpoint(0, archivedHead[1]), CancellationToken.None))
            .IsValid.Should().BeTrue();
        var regrown = await sut.VerifyChainAsync(new AuditChainCheckpoint(0, new string('a', 64)), CancellationToken.None);
        regrown.IsValid.Should().BeFalse();
        regrown.FailureReason.Should().Contain("differs from the one last verified");
    }

    [Fact]
    public async Task WithNothingArchivedYet_TheChainStillStartsAtGenesis()
    {
        // The archive is configured but empty: a live chain whose front was deleted is not "archived".
        using var sut = Writer();
        await ThreeDaysAsync(sut);
        File.Delete(LiveDay("2026-10-19"));

        var verified = await sut.VerifyChainAsync(CancellationToken.None);

        verified.IsValid.Should().BeFalse();
        verified.FirstBrokenSequence.Should().Be(0);
    }
}
