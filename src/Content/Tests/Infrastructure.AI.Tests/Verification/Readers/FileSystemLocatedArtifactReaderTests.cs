using FluentAssertions;
using Infrastructure.AI.Tools;
using Infrastructure.AI.Verification.Readers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Verification.Readers;

/// <summary>
/// Proves <see cref="FileSystemLocatedArtifactReader"/> against a real, sandboxed
/// <see cref="FileSystemService"/> (not mocked) — so the path-traversal refusal test exercises the
/// actual <c>SandboxedPathGuard</c> a claim's location string would hit, not a stand-in for it.
/// </summary>
public sealed class FileSystemLocatedArtifactReaderTests : IDisposable
{
    private readonly string _root;
    private readonly FileSystemLocatedArtifactReader _sut;

    public FileSystemLocatedArtifactReaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"file-reader-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        var fileSystem = new FileSystemService(NullLogger<FileSystemService>.Instance, [_root]);
        _sut = new FileSystemLocatedArtifactReader(fileSystem, NullLogger<FileSystemLocatedArtifactReader>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task TryReadAsync_ExistingFile_ReturnsContent()
    {
        File.WriteAllText(Path.Combine(_root, "Foo.cs"), "class Foo { }");

        var content = await _sut.TryReadAsync("file:Foo.cs", CancellationToken.None);

        content.Should().Contain("class Foo { }");
    }

    [Fact]
    public async Task TryReadAsync_FileDoesNotExist_ReturnsNull()
    {
        var content = await _sut.TryReadAsync("file:DoesNotExist.cs", CancellationToken.None);

        content.Should().BeNull();
    }

    [Fact]
    public async Task TryReadAsync_LineNumberSuffix_StripsSuffixAndPrependsHint()
    {
        File.WriteAllText(Path.Combine(_root, "Foo.cs"), "class Foo { }");

        var content = await _sut.TryReadAsync("file:Foo.cs:42", CancellationToken.None);

        content.Should().Contain("line 42");
        content.Should().Contain("class Foo { }");
    }

    [Fact]
    public async Task TryReadAsync_NotFileScheme_ReturnsNull()
    {
        var content = await _sut.TryReadAsync("config:AI.SomeField", CancellationToken.None);

        content.Should().BeNull();
    }

    [Fact]
    public async Task TryReadAsync_EmptyPathAfterScheme_ReturnsNull()
    {
        var content = await _sut.TryReadAsync("file:", CancellationToken.None);

        content.Should().BeNull();
    }

    // THE security-critical mutation: a claim citing a path outside the sandbox is refused, not
    // read — this is SandboxedPathGuard's own traversal check firing through the reader, proven
    // against the real guard rather than a mock that could silently no-op it. A real secret file
    // sits exactly where "../outside.txt" resolves to, so a null result here proves refusal, not a
    // coincidental "not found."
    [Fact]
    public async Task TryReadAsync_PathTraversal_IsRefusedNotRead()
    {
        var secretPath = Path.Combine(Path.GetTempPath(), $"file-reader-secret-{Guid.NewGuid():N}.txt");
        File.WriteAllText(secretPath, "SECRET CONTENT");
        try
        {
            var content = await _sut.TryReadAsync($"file:../{Path.GetFileName(secretPath)}", CancellationToken.None);

            content.Should().BeNull();
        }
        finally
        {
            File.Delete(secretPath);
        }
    }

    [Fact]
    public async Task TryReadAsync_DeepPathTraversal_IsRefusedNotRead()
    {
        var content = await _sut.TryReadAsync("file:../../../../etc/passwd", CancellationToken.None);

        content.Should().BeNull();
    }
}
