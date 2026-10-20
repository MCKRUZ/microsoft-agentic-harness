using FluentAssertions;
using Infrastructure.AI.Audit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Infrastructure.AI.Tests.Audit;

/// <summary>
/// #416: every audit log is stored one file per day, so finished days can later be sealed and moved off the host.
/// A log written before that split keeps its single file, and the chain continues from it into the day-files.
/// </summary>
public sealed class DatedAuditSegmentsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _legacyFile;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 19, 23, 30, 0, TimeSpan.Zero));

    public DatedAuditSegmentsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"dated-audit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _legacyFile = Path.Combine(_tempDir, "governance.jsonl");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private DatedAuditSegments Layout() => DatedAuditSegments.BesideLegacyFile(_legacyFile, _clock);

    private HashChainedJsonlWriter Writer() => new(Layout(), _legacyFile + ".lock", NullLogger.Instance);

    [Fact]
    public void Ordered_PutsTheEarlierSingleFileFirst_ThenOnlyTheDayFilesByDate()
    {
        var folder = Path.Combine(_tempDir, "governance");
        Directory.CreateDirectory(folder);
        foreach (var name in new[] { "2026-10-20.jsonl", "2026-10-02.jsonl", "notes.jsonl", "2026-13-01.jsonl", "2026-10-21.jsonl.bak" })
            File.WriteAllText(Path.Combine(folder, name), "x\n");
        File.WriteAllText(_legacyFile, "x\n");

        Layout().Ordered().Select(Path.GetFileName).Should().Equal(
            "governance.jsonl", "2026-10-02.jsonl", "2026-10-20.jsonl");
    }

    [Fact]
    public void Current_IsTheUtcDay()
    {
        var layout = new DatedAuditSegments(_tempDir, legacyFile: null,
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 19, 23, 30, 0, TimeSpan.FromHours(-5))));

        Path.GetFileName(layout.Current()).Should().Be("2026-10-20.jsonl");
    }

    [Fact]
    public async Task ALogWrittenToOneFile_ContinuesIntoDayFiles_AsOneChain()
    {
        using (var before = new HashChainedJsonlWriter(_legacyFile, NullLogger.Instance))
        {
            (await before.AppendAsync("{\"n\":0}", CancellationToken.None)).IsSuccess.Should().BeTrue();
            (await before.AppendAsync("{\"n\":1}", CancellationToken.None)).IsSuccess.Should().BeTrue();
        }
        var legacyBefore = await File.ReadAllTextAsync(_legacyFile);
        var legacyHead = legacyBefore.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1].Split('\t')[1];

        using var after = Writer();
        (await after.AppendAsync("{\"n\":2}", CancellationToken.None)).IsSuccess.Should().BeTrue();

        (await File.ReadAllTextAsync(_legacyFile)).Should().Be(legacyBefore, "the earlier file is finished and never appended to again");
        var dayLine = (await File.ReadAllLinesAsync(Path.Combine(_tempDir, "governance", "2026-10-19.jsonl"))).Single().Split('\t');
        dayLine[2].Should().Be(legacyHead, "the first day-file record chains onto the single file's last record");
        dayLine[3].Should().Be("2");

        var verified = await after.VerifyChainAsync(CancellationToken.None);
        verified.IsValid.Should().BeTrue();
        verified.VerifiedCount.Should().Be(3);
    }

    [Fact]
    public async Task ANewDay_StartsANewFile_AndTheChainRunsAcrossThem()
    {
        using var sut = Writer();
        (await sut.AppendAsync("{\"n\":0}", CancellationToken.None)).IsSuccess.Should().BeTrue();
        _clock.Advance(TimeSpan.FromHours(1)); // past midnight UTC
        (await sut.AppendAsync("{\"n\":1}", CancellationToken.None)).IsSuccess.Should().BeTrue();

        Layout().Ordered().Select(Path.GetFileName).Should().Equal("2026-10-19.jsonl", "2026-10-20.jsonl");
        File.Exists(_legacyFile).Should().BeFalse("a log that starts out split by day never creates the single file");

        var verified = await sut.VerifyChainAsync(CancellationToken.None);
        verified.IsValid.Should().BeTrue();
        verified.VerifiedCount.Should().Be(2);
    }

    [Fact]
    public async Task AWriterWhoseClockIsBehind_AppendsToTheLatestDay_NotAnEarlierOne()
    {
        using (var ahead = new HashChainedJsonlWriter(
            DatedAuditSegments.BesideLegacyFile(_legacyFile, new FakeTimeProvider(_clock.GetUtcNow().AddDays(2))),
            _legacyFile + ".lock", NullLogger.Instance))
        {
            (await ahead.AppendAsync("{\"n\":0}", CancellationToken.None)).IsSuccess.Should().BeTrue();
        }

        using var behind = Writer();
        (await behind.AppendAsync("{\"n\":1}", CancellationToken.None)).IsSuccess.Should().BeTrue();

        Layout().Ordered().Select(Path.GetFileName).Should().Equal("2026-10-21.jsonl");
        (await behind.VerifyChainAsync(CancellationToken.None)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task AFileInTheFolderThatIsNotADay_IsNeitherWrittenNorReadAsPartOfTheChain()
    {
        using var sut = Writer();
        (await sut.AppendAsync("{\"n\":0}", CancellationToken.None)).IsSuccess.Should().BeTrue();
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "governance", "zzz.jsonl"), "{\"planted\":true}\n");

        (await sut.AppendAsync("{\"n\":1}", CancellationToken.None)).IsSuccess.Should().BeTrue();

        var payloads = new List<string>();
        await foreach (var payload in sut.ReadAllPayloadsAsync(CancellationToken.None))
            payloads.Add(payload);
        payloads.Should().Equal("{\"n\":0}", "{\"n\":1}");
        (await sut.VerifyChainAsync(CancellationToken.None)).IsValid.Should().BeTrue();
    }
}
