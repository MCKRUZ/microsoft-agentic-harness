using System.Text;
using FluentAssertions;
using Infrastructure.AI.Changes;
using Infrastructure.AI.Tests.Changes.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Changes;

public sealed class FileSystemEvidenceStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileSystemEvidenceStore _sut;

    public FileSystemEvidenceStoreTests()
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
    public async Task Store_ProducesShaPrefixedHash()
    {
        var bytes = Encoding.UTF8.GetBytes("hello world");
        var hash = await _sut.StoreAsync(bytes, "text/plain", CancellationToken.None);
        hash.Should().StartWith("sha256:");
    }

    [Fact]
    public async Task StoreRetrieve_RoundTripsBytes()
    {
        var bytes = Encoding.UTF8.GetBytes("evidence payload");
        var hash = await _sut.StoreAsync(bytes, "text/plain", CancellationToken.None);

        var retrieved = await _sut.RetrieveAsync(hash, CancellationToken.None);

        retrieved.HasValue.Should().BeTrue();
        Encoding.UTF8.GetString(retrieved!.Value.Span).Should().Be("evidence payload");
    }

    [Fact]
    public async Task Store_DuplicateContent_ReturnsSameHashAndIsIdempotent()
    {
        var bytes = Encoding.UTF8.GetBytes("same content");

        var hashA = await _sut.StoreAsync(bytes, "text/plain", CancellationToken.None);
        var hashB = await _sut.StoreAsync(bytes, "text/plain", CancellationToken.None);

        hashA.Should().Be(hashB);
    }

    [Fact]
    public async Task Store_DifferentContent_ProducesDifferentHash()
    {
        var hashA = await _sut.StoreAsync(Encoding.UTF8.GetBytes("a"), "text/plain", CancellationToken.None);
        var hashB = await _sut.StoreAsync(Encoding.UTF8.GetBytes("b"), "text/plain", CancellationToken.None);

        hashA.Should().NotBe(hashB);
    }

    [Fact]
    public async Task Retrieve_UnknownHash_ReturnsNull()
    {
        var retrieved = await _sut.RetrieveAsync("sha256:nonexistent", CancellationToken.None);
        retrieved.Should().BeNull();
    }
}
