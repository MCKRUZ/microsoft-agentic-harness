using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Models;
using FluentAssertions;
using Infrastructure.AI.Tools;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Tools;

/// <summary>
/// Tests for <see cref="FileSystemTool"/> covering operation dispatch,
/// error handling, and parameter extraction.
/// </summary>
public sealed class FileSystemToolTests
{
    private readonly Mock<IFileSystemService> _fileSystem = new();
    private readonly FileSystemTool _sut;

    public FileSystemToolTests()
    {
        _sut = new FileSystemTool(_fileSystem.Object);
    }

    [Fact]
    public void Name_ReturnsFileSystem()
    {
        _sut.Name.Should().Be("file_system");
    }

    [Fact]
    public void Description_IsNotEmpty()
    {
        _sut.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void SupportedOperations_ContainsAllExpected()
    {
        _sut.SupportedOperations.Should().BeEquivalentTo(
            ["read", "write", "list", "search", "exists"]);
    }

    [Fact]
    public async Task ExecuteAsync_Read_DelegatesAndReturnsContent()
    {
        _fileSystem
            .Setup(x => x.ReadFileAsync("test.txt", It.IsAny<CancellationToken>()))
            .ReturnsAsync("file content");

        var result = await _sut.ExecuteAsync("read",
            new Dictionary<string, object?> { ["path"] = "test.txt" });

        result.Success.Should().BeTrue();
        result.Output.Should().Be("file content");
    }

    [Fact]
    public async Task ExecuteAsync_Write_DelegatesAndReportsCharCount()
    {
        var result = await _sut.ExecuteAsync("write",
            new Dictionary<string, object?>
            {
                ["path"] = "out.txt",
                ["content"] = "hello world"
            });

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("11 characters");
        _fileSystem.Verify(x => x.WriteFileAsync("out.txt", "hello world", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_List_ReturnsEntries()
    {
        _fileSystem
            .Setup(x => x.ListDirectoryAsync("src", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "file1.cs", "file2.cs" });

        var result = await _sut.ExecuteAsync("list",
            new Dictionary<string, object?> { ["path"] = "src" });

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("file1.cs");
        result.Output.Should().Contain("file2.cs");
    }

    [Fact]
    public async Task ExecuteAsync_List_WithPattern_PassesPatternThrough()
    {
        _fileSystem
            .Setup(x => x.ListDirectoryAsync("src", "*.cs", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "file1.cs" });

        var result = await _sut.ExecuteAsync("list",
            new Dictionary<string, object?>
            {
                ["path"] = "src",
                ["pattern"] = "*.cs"
            });

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_Search_WithResults_FormatsOutput()
    {
        _fileSystem
            .Setup(x => x.SearchFilesAsync("src", "Main", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FileSearchResult>
            {
                new() { FilePath = "Program.cs", Snippet = "static void Main()", LineNumber = 5 }
            });

        var result = await _sut.ExecuteAsync("search",
            new Dictionary<string, object?>
            {
                ["path"] = "src",
                ["search_term"] = "Main"
            });

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("Program.cs:5: static void Main()");
    }

    [Fact]
    public async Task ExecuteAsync_Search_NoResults_ReportsNoMatches()
    {
        _fileSystem
            .Setup(x => x.SearchFilesAsync("src", "xyz", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FileSearchResult>());

        var result = await _sut.ExecuteAsync("search",
            new Dictionary<string, object?>
            {
                ["path"] = "src",
                ["search_term"] = "xyz"
            });

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("No matches found");
    }

    [Fact]
    public async Task ExecuteAsync_Search_WithoutLineNumber_OmitsLineNumber()
    {
        _fileSystem
            .Setup(x => x.SearchFilesAsync("src", "query", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FileSearchResult>
            {
                new() { FilePath = "file.txt", Snippet = "matched text", LineNumber = null }
            });

        var result = await _sut.ExecuteAsync("search",
            new Dictionary<string, object?>
            {
                ["path"] = "src",
                ["search_term"] = "query"
            });

        result.Output.Should().Be("file.txt: matched text");
    }

    [Fact]
    public async Task ExecuteAsync_Exists_True_ReturnsTrue()
    {
        _fileSystem
            .Setup(x => x.ExistsAsync("test.txt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _sut.ExecuteAsync("exists",
            new Dictionary<string, object?> { ["path"] = "test.txt" });

        result.Success.Should().BeTrue();
        result.Output.Should().Be("true");
    }

    [Fact]
    public async Task ExecuteAsync_Exists_False_ReturnsFalse()
    {
        _fileSystem
            .Setup(x => x.ExistsAsync("missing.txt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _sut.ExecuteAsync("exists",
            new Dictionary<string, object?> { ["path"] = "missing.txt" });

        result.Output.Should().Be("false");
    }

    [Fact]
    public async Task ExecuteAsync_UnknownOperation_ReturnsFail()
    {
        var result = await _sut.ExecuteAsync("delete",
            new Dictionary<string, object?> { ["path"] = "file.txt" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Unknown operation");
        result.Error.Should().Contain("delete");
    }

    [Fact]
    public async Task ExecuteAsync_MissingRequiredParam_ReturnsFail()
    {
        var result = await _sut.ExecuteAsync("read",
            new Dictionary<string, object?>());

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Invalid path or parameters");
    }

    [Fact]
    public async Task ExecuteAsync_UnauthorizedAccess_ReturnsFail()
    {
        _fileSystem
            .Setup(x => x.ReadFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnauthorizedAccessException());

        var result = await _sut.ExecuteAsync("read",
            new Dictionary<string, object?> { ["path"] = "/etc/passwd" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Access denied");
    }

    [Fact]
    public async Task ExecuteAsync_FileNotFound_ReturnsFail()
    {
        _fileSystem
            .Setup(x => x.ReadFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FileNotFoundException());

        var result = await _sut.ExecuteAsync("read",
            new Dictionary<string, object?> { ["path"] = "missing.txt" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("File not found");
    }

    [Fact]
    public async Task ExecuteAsync_DirectoryNotFound_ReturnsFail()
    {
        _fileSystem
            .Setup(x => x.ListDirectoryAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DirectoryNotFoundException());

        var result = await _sut.ExecuteAsync("list",
            new Dictionary<string, object?> { ["path"] = "/nonexistent" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Directory not found");
    }

    [Fact]
    public async Task ExecuteAsync_IOException_ReturnsFail()
    {
        _fileSystem
            .Setup(x => x.ReadFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Disk full"));

        var result = await _sut.ExecuteAsync("read",
            new Dictionary<string, object?> { ["path"] = "file.txt" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("I/O error");
    }

    [Fact]
    public async Task ExecuteAsync_CaseInsensitiveOperation_Works()
    {
        _fileSystem
            .Setup(x => x.ExistsAsync("file.txt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _sut.ExecuteAsync("EXISTS",
            new Dictionary<string, object?> { ["path"] = "file.txt" });

        result.Success.Should().BeTrue();
    }

    [Fact]
    public void Constructor_NullFileSystem_Throws()
    {
        var act = () => new FileSystemTool(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
