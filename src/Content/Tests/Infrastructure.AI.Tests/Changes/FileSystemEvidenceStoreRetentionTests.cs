using System.Text;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Changes;
using Infrastructure.AI.Tests.Changes.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Changes;

/// <summary>
/// Evidence ages out by when it was last stored (#901), never while an open proposal points at it, and the sweep deletes
/// only what this store names: the audit files beside the evidence, and anything else in its folders, survive.
/// </summary>
public sealed class FileSystemEvidenceStoreRetentionTests : IDisposable
{
    private static readonly DateTimeOffset Cutoff = DateTimeOffset.UtcNow.AddDays(-90);
    private static readonly DateTime Old = DateTime.UtcNow.AddDays(-120);
    private static readonly IReadOnlySet<string> NoneInUse = new HashSet<string>();

    private readonly string _tempDir;
    private readonly string _root;
    private readonly AppConfig _config;
    private readonly FileSystemEvidenceStore _sut;

    public FileSystemEvidenceStoreRetentionTests()
    {
        var (monitor, dir) = TestConfig.NewMonitor();
        _tempDir = dir;
        _config = monitor.CurrentValue;
        _root = _config.AI.Changes.EvidenceStoragePath;
        _sut = new FileSystemEvidenceStore(monitor, NullLogger<FileSystemEvidenceStore>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task EvidenceNotStoredSinceTheCutoff_IsDeletedWithItsSidecar_AndRecentEvidenceIsKept()
    {
        var old = await StoreAsync("old evidence");
        var recent = await StoreAsync("recent evidence");
        Age(old, Old);

        var deleted = await _sut.DeleteNotStoredSinceAsync(Cutoff, NoneInUse, 100, CancellationToken.None);

        deleted.Should().Be(1);
        File.Exists(BlobPath(old)).Should().BeFalse();
        File.Exists(BlobPath(old) + ".contenttype").Should().BeFalse();
        File.Exists(BlobPath(recent)).Should().BeTrue();
    }

    [Fact]
    public async Task EvidenceAnOpenProposalPointsAt_IsKept_HoweverOld()
    {
        var hash = await StoreAsync("evidence a proposal is waiting on");
        Age(hash, Old);

        var deleted = await _sut.DeleteNotStoredSinceAsync(
            Cutoff, new HashSet<string> { hash }, 100, CancellationToken.None);

        deleted.Should().Be(0);
        File.Exists(BlobPath(hash)).Should().BeTrue();
    }

    [Fact]
    public async Task StoringTheSameEvidenceAgain_RenewsIt()
    {
        // Content addressing skips rewriting an existing blob, so without renewing its time a blob stored again
        // yesterday would still age from its first write.
        var hash = await StoreAsync("evidence stored twice");
        Age(hash, Old);

        await StoreAsync("evidence stored twice");

        (await _sut.DeleteNotStoredSinceAsync(Cutoff, NoneInUse, 100, CancellationToken.None)).Should().Be(0);
        File.Exists(BlobPath(hash)).Should().BeTrue();
    }

    [Theory]
    [InlineData("ab/notes.bin")]
    [InlineData("ab/abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG.bin.bak")]
    [InlineData("xy/abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG.bin")]
    [InlineData("AB/abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG.bin")]
    public async Task AFileThisStoreWouldNotHaveNamed_IsKept(string relative)
    {
        // The right name in the wrong prefix folder (including another case of the right one) was not written here.
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "not evidence");
        File.SetLastWriteTimeUtc(path, Old);

        (await _sut.DeleteNotStoredSinceAsync(Cutoff, NoneInUse, 100, CancellationToken.None)).Should().Be(0);
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task TheAuditTrailBesideTheEvidence_IsNeverReached()
    {
        var audit = Path.Combine(_root, "..", "changes-audit-2026-01-01.jsonl");
        Directory.CreateDirectory(_root);
        File.WriteAllText(audit, "{}");
        File.SetLastWriteTimeUtc(audit, Old);

        (await _sut.DeleteNotStoredSinceAsync(Cutoff, NoneInUse, 100, CancellationToken.None)).Should().Be(0);
        File.Exists(audit).Should().BeTrue();
    }

    [Fact]
    public async Task AStagingFileACrashLeftBehind_GoesOnceAsOld()
    {
        var hash = await StoreAsync("kept evidence");
        var name = Path.GetFileName(BlobPath(hash));
        var orphan = Path.Combine(Path.GetDirectoryName(BlobPath(hash))!, name + ".k3j4h5g6.x1z.tmp");
        await File.WriteAllTextAsync(orphan, "half-written evidence");
        File.SetLastWriteTimeUtc(orphan, Old);

        await _sut.DeleteNotStoredSinceAsync(Cutoff, NoneInUse, 100, CancellationToken.None);

        File.Exists(orphan).Should().BeFalse();
        File.Exists(BlobPath(hash)).Should().BeTrue();
    }

    [Fact]
    public async Task OneSweep_RemovesNoMoreThanItsCap()
    {
        var hashes = new List<string>();
        for (var i = 0; i < 3; i++)
            hashes.Add(await StoreAsync($"evidence {i}"));
        hashes.ForEach(h => Age(h, Old));

        (await _sut.DeleteNotStoredSinceAsync(Cutoff, NoneInUse, 2, CancellationToken.None)).Should().Be(2);
        hashes.Count(h => File.Exists(BlobPath(h))).Should().Be(1);
    }

    [Theory]
    [InlineData(true, 90, 90)]
    [InlineData(false, 90, null)]
    [InlineData(true, 0.5, null)]
    public void TheReceiptPeriod_IsReportedOnlyWhileTheSweepRuns(bool enabled, double days, int? expectedDays)
    {
        _config.AI.Changes.EvidenceRetention.Enabled = enabled;
        _config.AI.Changes.EvidenceRetention.RetainFor = TimeSpan.FromDays(days);

        _sut.CurrentRetentionPeriod.Should().Be(expectedDays is { } d ? TimeSpan.FromDays(d) : null);
    }

    private Task<string> StoreAsync(string text) =>
        _sut.StoreAsync(Encoding.UTF8.GetBytes(text), "text/plain", CancellationToken.None);

    private string BlobPath(string hash)
    {
        var name = hash["sha256:".Length..];
        return Path.Combine(_root, name[..2], name + ".bin");
    }

    private void Age(string hash, DateTime when)
    {
        File.SetLastWriteTimeUtc(BlobPath(hash), when);
        File.SetLastWriteTimeUtc(BlobPath(hash) + ".contenttype", when);
    }
}
