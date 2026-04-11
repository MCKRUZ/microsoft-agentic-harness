using FluentAssertions;
using Infrastructure.AI.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Tools;

/// <summary>
/// Tests for FileSystemService — sandbox path enforcement and search behavior.
/// </summary>
public sealed class FileSystemServiceTests : IDisposable
{
    private readonly string _root;
    private readonly FileSystemService _sut;

    public FileSystemServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"fss-tests-{Guid.NewGuid():N}");
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

    // ── SearchFilesAsync: skip-directory regression tests ──────────────────────
    // Before the fix, searching from a directory containing .git or bin would
    // exhaust the 1000-file scan limit before reaching actual source files.

    [Fact]
    public async Task SearchFilesAsync_DoesNotScanGitDirectory()
    {
        // Arrange — put the target file in src/ and a red-herring in .git/
        var srcDir = CreateDir("src");
        var gitDir = CreateDir(".git");
        WriteFile(srcDir, "Target.cs", "class ExecuteAgentTurnCommand { }");
        WriteFile(gitDir, "COMMIT_EDITMSG", "ExecuteAgentTurnCommand");  // should be skipped

        // Act
        var results = await _sut.SearchFilesAsync(_root, "ExecuteAgentTurnCommand");

        // Assert — found in src/, NOT from .git/
        results.Should().ContainSingle();
        results[0].FilePath.Should().Contain("Target.cs");
        results[0].FilePath.Should().NotContain(".git");
    }

    [Fact]
    public async Task SearchFilesAsync_DoesNotScanBinDirectory()
    {
        // Arrange
        var srcDir = CreateDir("src");
        var binDir = CreateDir("bin");
        WriteFile(srcDir, "Handler.cs", "class MyHandler { }");
        WriteFile(binDir, "Handler.dll.config", "class MyHandler { }");

        // Act
        var results = await _sut.SearchFilesAsync(_root, "MyHandler");

        // Assert — only the .cs file, not the bin/ file
        results.Should().ContainSingle();
        results[0].FilePath.Should().Contain("Handler.cs");
    }

    [Fact]
    public async Task SearchFilesAsync_DoesNotScanObjDirectory()
    {
        var srcDir = CreateDir("src");
        var objDir = CreateDir("obj");
        WriteFile(srcDir, "Command.cs", "SearchTarget");
        WriteFile(objDir, "Command.g.cs", "SearchTarget");

        var results = await _sut.SearchFilesAsync(_root, "SearchTarget");

        results.Should().ContainSingle();
        results[0].FilePath.Should().Contain("Command.cs");
    }

    [Fact]
    public async Task SearchFilesAsync_FindsFilesInNestedSourceDirectories()
    {
        // Arrange — deep nested path like src/Content/Application/Core/CQRS/
        var deep = CreateDir("src", "Content", "Application", "Core", "CQRS");
        WriteFile(deep, "ExecuteAgentTurnCommand.cs", "public class ExecuteAgentTurnCommand { }");

        // Act
        var results = await _sut.SearchFilesAsync(_root, "ExecuteAgentTurnCommand");

        // Assert
        results.Should().NotBeEmpty();
        results.Should().Contain(r => r.FilePath.Contains("ExecuteAgentTurnCommand.cs"));
    }

    [Fact]
    public async Task SearchFilesAsync_NoMatches_ReturnsEmptyList()
    {
        CreateDir("src");
        WriteFile(Path.Combine(_root, "src"), "Unrelated.cs", "public class Unrelated { }");

        var results = await _sut.SearchFilesAsync(_root, "NonExistentTerm_XYZ");

        results.Should().BeEmpty();
    }

    // ── Sandbox enforcement tests ───────────────────────────────────────────────

    [Fact]
    public async Task ReadFileAsync_PathOutsideSandbox_ThrowsUnauthorizedAccess()
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
    public async Task ListDirectoryAsync_ValidPath_ReturnsEntries()
    {
        var sub = CreateDir("mysub");
        WriteFile(sub, "file.txt", "content");

        var entries = await _sut.ListDirectoryAsync(_root);

        entries.Should().Contain("mysub/");
    }

    [Fact]
    public async Task ExistsAsync_ExistingFile_ReturnsTrue()
    {
        WriteFile(_root, "exists.txt", "hi");

        var result = await _sut.ExistsAsync(Path.Combine(_root, "exists.txt"));

        result.Should().BeTrue();
    }

    [Fact]
    public async Task ExistsAsync_NonExistentPath_ReturnsFalse()
    {
        var result = await _sut.ExistsAsync(Path.Combine(_root, "ghost.txt"));

        result.Should().BeFalse();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private string CreateDir(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteFile(string dir, string name, string content) =>
        File.WriteAllText(Path.Combine(dir, name), content);
}
