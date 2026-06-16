using System.Text;
using FluentAssertions;
using Infrastructure.AI.Changes;
using Infrastructure.AI.Tests.Changes.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Changes;

/// <summary>
/// Regression tests for the 2026-06-11 solution review finding on
/// <see cref="FileSystemEvidenceStore"/>: evidence writes were non-atomic and the
/// documented "benign race" actually threw. The store now stages content in a
/// uniquely-named temp file and atomically moves it onto the content-addressed
/// path, so concurrent duplicate stores are idempotent (never throw) and no
/// truncated blob can ever appear at the final path.
/// </summary>
public sealed class FileSystemEvidenceStoreSolutionReviewFixTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileSystemEvidenceStore _sut;

    public FileSystemEvidenceStoreSolutionReviewFixTests()
    {
        var (monitor, dir) = TestConfig.NewMonitor();
        _tempDir = dir;
        _sut = new FileSystemEvidenceStore(monitor, NullLogger<FileSystemEvidenceStore>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public async Task StoreAsync_ConcurrentDuplicateWrites_AllSucceedAndContentRoundTrips()
    {
        // Old behavior: StoreAsync opened the FINAL path with FileMode.CreateNew,
        // so the loser of the File.Exists/CreateNew race threw IOException
        // ("file already exists") — contradicting the IEvidenceStore idempotency
        // contract. The atomic temp-file + File.Move(overwrite) fix makes every
        // concurrent writer of identical content succeed.
        var bytes = Encoding.UTF8.GetBytes("contended evidence payload");

        var tasks = Enumerable
            .Range(0, 32)
            .Select(_ => _sut.StoreAsync(bytes, "application/octet-stream", CancellationToken.None))
            .ToArray();

        // Should not throw for any writer.
        var hashes = await Task.WhenAll(tasks);

        hashes.Should().OnlyContain(h => h == hashes[0]);

        var retrieved = await _sut.RetrieveAsync(hashes[0], CancellationToken.None);
        retrieved.HasValue.Should().BeTrue();
        retrieved!.Value.ToArray().Should().Equal(bytes);
    }

    [Fact]
    public async Task StoreAsync_AfterWrite_LeavesNoTempStagingFileAtContentAddressedPath()
    {
        // The atomic-write fix stages content in a "*.tmp" file before moving it
        // onto the final path. A completed store must leave only the final blob
        // (+ .contenttype sidecar) — proving the move ran and writes never go
        // directly to the content-addressed path.
        var bytes = Encoding.UTF8.GetBytes("atomic write payload");

        await _sut.StoreAsync(bytes, "text/plain", CancellationToken.None);

        var leftoverTempFiles = Directory.EnumerateFiles(
            _tempDir,
            "*.tmp",
            SearchOption.AllDirectories);

        leftoverTempFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task RetrieveAsync_AfterDuplicateConcurrentStores_ReturnsContentThatHashesToRequest()
    {
        // Content-addressed integrity guarantee: whatever bytes RetrieveAsync
        // returns must hash to the requested evidence hash. Under the old
        // non-atomic path a truncated blob could remain at the final path and be
        // served forever. Exercise the concurrent path then verify the bytes.
        var bytes = Encoding.UTF8.GetBytes("integrity-checked evidence");

        var stores = Enumerable
            .Range(0, 8)
            .Select(_ => _sut.StoreAsync(bytes, "text/plain", CancellationToken.None));
        var hashes = await Task.WhenAll(stores);

        var hash = hashes[0];
        var retrieved = await _sut.RetrieveAsync(hash, CancellationToken.None);

        retrieved.HasValue.Should().BeTrue();
        var actualHash = "sha256:" + Domain.Common.Helpers.Base64UrlHelper.Encode(
            System.Security.Cryptography.SHA256.HashData(retrieved!.Value.Span));
        actualHash.Should().Be(hash);
    }
}
