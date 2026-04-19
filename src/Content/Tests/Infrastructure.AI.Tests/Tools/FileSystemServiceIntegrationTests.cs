using FluentAssertions;
using Infrastructure.AI.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Tools;

/// <summary>
/// Integration tests for <see cref="FileSystemService"/> covering ReadFileAsync,
/// WriteFileAsync, ListDirectoryAsync, SearchFilesAsync, and ExistsAsync with
/// edge cases not covered by existing tests.
/// </summary>
public sealed class FileSystemServiceIntegrationTests : IDisposable
{
    private readonly string _root;
    private readonly FileSystemService _sut;

    public FileSystemServiceIntegrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"fss-int-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        _sut = new FileSystemService(
            NullLogger<FileSystemService>.Instance,
            [_root]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string CreateDir(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private string WriteFile(string dir, string name, string content)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    // ── ReadFileAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadFileAsync_ExistingFile_ReturnsContent()
    {
        var filePath = WriteFile(_root, "readme.txt", "Hello, World!");

        var content = await _sut.ReadFileAsync(filePath);

        content.Should().Be("Hello, World!");
    }

    [Fact]
    public async Task ReadFileAsync_NonExistentFile_ThrowsFileNotFound()
    {
        var act = async () => await _sut.ReadFileAsync(Path.Combine(_root, "ghost.txt"));

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task ReadFileAsync_PathOutsideSandbox_ThrowsUnauthorized()
    {
        var outsidePath = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.txt");
        File.WriteAllText(outsidePath, "secret");

        try
        {
            var act = async () => await _sut.ReadFileAsync(outsidePath);
            await act.Should().ThrowAsync<UnauthorizedAccessException>();
        }
        finally
        {
            File.Delete(outsidePath);
        }
    }

    [Fact]
    public async Task ReadFileAsync_TraversalAttempt_ThrowsArgumentException()
    {
        var act = async () => await _sut.ReadFileAsync(Path.Combine(_root, "..", "escape.txt"));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ── WriteFileAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task WriteFileAsync_CreatesNewFile()
    {
        var filePath = Path.Combine(_root, "new-file.txt");

        await _sut.WriteFileAsync(filePath, "content here");

        File.Exists(filePath).Should().BeTrue();
        (await File.ReadAllTextAsync(filePath)).Should().Be("content here");
    }

    [Fact]
    public async Task WriteFileAsync_OverwritesExistingFile()
    {
        var filePath = WriteFile(_root, "overwrite.txt", "old content");

        await _sut.WriteFileAsync(filePath, "new content");

        (await File.ReadAllTextAsync(filePath)).Should().Be("new content");
    }

    [Fact]
    public async Task WriteFileAsync_CreatesIntermediateDirectories()
    {
        var filePath = Path.Combine(_root, "sub", "deep", "file.txt");

        await _sut.WriteFileAsync(filePath, "deep content");

        File.Exists(filePath).Should().BeTrue();
        (await File.ReadAllTextAsync(filePath)).Should().Be("deep content");
    }

    [Fact]
    public async Task WriteFileAsync_NullContent_ThrowsArgumentNull()
    {
        var act = async () => await _sut.WriteFileAsync(Path.Combine(_root, "null.txt"), null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task WriteFileAsync_PathOutsideSandbox_ThrowsUnauthorized()
    {
        var outsidePath = Path.Combine(Path.GetTempPath(), $"write-escape-{Guid.NewGuid():N}.txt");

        var act = async () => await _sut.WriteFileAsync(outsidePath, "content");

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    // ── ListDirectoryAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task ListDirectoryAsync_ListsFilesAndDirectories()
    {
        var sub = CreateDir("subdir");
        WriteFile(_root, "file1.txt", "a");
        WriteFile(_root, "file2.cs", "b");

        var entries = await _sut.ListDirectoryAsync(_root);

        entries.Should().Contain("subdir/");
        entries.Should().Contain("file1.txt");
        entries.Should().Contain("file2.cs");
    }

    [Fact]
    public async Task ListDirectoryAsync_WithPattern_FiltersFiles()
    {
        WriteFile(_root, "code.cs", "class A {}");
        WriteFile(_root, "readme.txt", "info");
        WriteFile(_root, "test.cs", "class B {}");

        var entries = await _sut.ListDirectoryAsync(_root, "*.cs");

        entries.Should().Contain("code.cs");
        entries.Should().Contain("test.cs");
        entries.Should().NotContain("readme.txt");
        // When pattern is specified, directories are NOT listed
        entries.Should().OnlyContain(e => !e.EndsWith("/"));
    }

    [Fact]
    public async Task ListDirectoryAsync_NonExistentDirectory_ThrowsDirectoryNotFound()
    {
        var act = async () => await _sut.ListDirectoryAsync(Path.Combine(_root, "nope"));

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [Fact]
    public async Task ListDirectoryAsync_PatternWithPathSeparator_ThrowsArgument()
    {
        var act = async () => await _sut.ListDirectoryAsync(_root, "sub/file.*");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ── SearchFilesAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task SearchFilesAsync_ReturnsMatchingLinesWithLineNumbers()
    {
        WriteFile(_root, "multi.txt", "line one\nfind this line\nline three");

        var results = await _sut.SearchFilesAsync(_root, "find this");

        results.Should().ContainSingle();
        results[0].LineNumber.Should().Be(2);
        results[0].Snippet.Should().Contain("find this line");
    }

    [Fact]
    public async Task SearchFilesAsync_CaseInsensitive()
    {
        WriteFile(_root, "case.txt", "Hello WORLD");

        var results = await _sut.SearchFilesAsync(_root, "hello world");

        results.Should().ContainSingle();
    }

    [Fact]
    public async Task SearchFilesAsync_WithPattern_FiltersFileTypes()
    {
        WriteFile(_root, "match.cs", "SearchTerm here");
        WriteFile(_root, "match.txt", "SearchTerm here too");

        var results = await _sut.SearchFilesAsync(_root, "SearchTerm", "*.cs");

        results.Should().ContainSingle();
        results[0].FilePath.Should().Contain("match.cs");
    }

    [Fact]
    public async Task SearchFilesAsync_NonExistentDirectory_ThrowsDirectoryNotFound()
    {
        var act = async () => await _sut.SearchFilesAsync(
            Path.Combine(_root, "gone"), "term");

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [Fact]
    public async Task SearchFilesAsync_SkipsNodeModules()
    {
        var src = CreateDir("src");
        var nm = CreateDir("node_modules");
        WriteFile(src, "app.ts", "UniqueToken");
        WriteFile(nm, "lib.js", "UniqueToken");

        var results = await _sut.SearchFilesAsync(_root, "UniqueToken");

        results.Should().ContainSingle();
        results[0].FilePath.Should().Contain("app.ts");
    }

    [Fact]
    public async Task SearchFilesAsync_MultipleMatchesInSameFile()
    {
        WriteFile(_root, "multi-match.txt",
            "first match\nsecond line\nthird match\nfourth line");

        var results = await _sut.SearchFilesAsync(_root, "match");

        results.Should().HaveCount(2);
        results.Should().Contain(r => r.LineNumber == 1);
        results.Should().Contain(r => r.LineNumber == 3);
    }

    [Fact]
    public async Task SearchFilesAsync_PatternWithPathSeparator_ThrowsArgument()
    {
        var act = async () => await _sut.SearchFilesAsync(_root, "term", "sub/file.*");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ── ExistsAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ExistsAsync_ExistingDirectory_ReturnsTrue()
    {
        var dir = CreateDir("check-dir");

        var exists = await _sut.ExistsAsync(dir);

        exists.Should().BeTrue();
    }

    [Fact]
    public async Task ExistsAsync_PathOutsideSandbox_ReturnsFalse()
    {
        var outsidePath = Path.Combine(Path.GetTempPath(), $"ext-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsidePath);

        try
        {
            var exists = await _sut.ExistsAsync(outsidePath);
            exists.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(outsidePath);
        }
    }

    // ── Zero allowed base paths ──────────────────────────────────────────────

    [Fact]
    public async Task Constructor_ZeroBasePaths_AllOperationsDenied()
    {
        var sut = new FileSystemService(
            NullLogger<FileSystemService>.Instance,
            []);

        var act = async () => await sut.ReadFileAsync(Path.Combine(_root, "test.txt"));

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    // ── Empty/whitespace paths ───────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ReadFileAsync_EmptyPath_ThrowsArgumentException(string path)
    {
        var act = async () => await _sut.ReadFileAsync(path);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WriteFileAsync_EmptyPath_ThrowsArgumentException(string path)
    {
        var act = async () => await _sut.WriteFileAsync(path, "content");

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
