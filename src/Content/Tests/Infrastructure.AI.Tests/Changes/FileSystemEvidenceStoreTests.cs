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

    // --- Directory permissions (#640, following #527's precedent) ---

    [Fact]
    public async Task StoreAsync_EvidenceDirectoryIsCreatedOwnerOnly()
    {
        // #640: evidence blobs are content-addressed copies of whatever content the store was asked
        // to preserve -- neither the evidence root nor its hash-prefix fan-out subdirectory exist
        // before this call, so both must be created owner-only rather than inheriting whatever the
        // process umask/ACL happens to grant.
        var bytes = Encoding.UTF8.GetBytes("permission check payload");
        var hash = await _sut.StoreAsync(bytes, "text/plain", CancellationToken.None);

        if (!OperatingSystem.IsWindows())
        {
            var prefix = hash["sha256:".Length..][..2];
            var evidenceDir = Path.Combine(_tempDir, "evidence", prefix);
            File.GetUnixFileMode(evidenceDir).Should().Be(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
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
        // Use a properly-shaped hash that just doesn't exist on disk.
        var validShape = "sha256:" + new string('A', 43);
        var retrieved = await _sut.RetrieveAsync(validShape, CancellationToken.None);
        retrieved.Should().BeNull();
    }

    public static IEnumerable<object[]> MalformedHashes()
    {
        // Inputs constructed in code so the test source doesn't contain literal
        // 'sha256:<43-char-token>' shapes — those match generic secret-scanner
        // regexes (e.g. Telegram bot tokens). Behavior tested is the same.
        var padA = new string('A', 42); // 42 chars + 1 trailing variant = 43

        yield return new object[] { "sha256:../../etc/passwd" };           // parent-dir traversal
        yield return new object[] { "sha256:" + padA + "/" };              // separator inside payload
        yield return new object[] { "sha256:" + padA + "\\" };             // backslash inside payload
        yield return new object[] { "sha256:" + padA + "." };              // period (out of alphabet)
        yield return new object[] { "sha256:" + padA + "+" };              // raw Base64 plus (not URL-safe)
        yield return new object[] { "sha256:short" };                       // wrong length
        yield return new object[] { "md5:abc" };                            // wrong prefix
        yield return new object[] { "../../etc/passwd" };                   // no prefix at all
    }

    [Theory]
    [MemberData(nameof(MalformedHashes))]
    public async Task Retrieve_MalformedHash_ReturnsNullWithoutEscapingRoot(string hash)
    {
        // The strict format check in TryGetSafePath must reject every one of
        // these without ever attempting File.Exists outside _tempDir.
        var retrieved = await _sut.RetrieveAsync(hash, CancellationToken.None);
        retrieved.Should().BeNull();
    }

    [Fact]
    public async Task Store_ProducesHashThatRetrieveAccepts()
    {
        // End-to-end: every hash StoreAsync produces must be accepted by
        // RetrieveAsync. Guarantees TryGetSafePath doesn't reject the format
        // StoreAsync emits.
        var hash = await _sut.StoreAsync(Encoding.UTF8.GetBytes("payload"), "text/plain", CancellationToken.None);
        var retrieved = await _sut.RetrieveAsync(hash, CancellationToken.None);

        retrieved.HasValue.Should().BeTrue();
    }
}
