using Domain.Common.Config;
using Domain.Common.Config.AI.ContextManagement;
using FluentAssertions;
using Infrastructure.AI.Context;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Context;

public sealed class FileSystemToolResultStoreTests : IDisposable
{
    private readonly FileSystemToolResultStore _sut;
    private readonly AppConfig _appConfig;
    private readonly string _tempDir;

    public FileSystemToolResultStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "toolresult-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _appConfig = new AppConfig();
        _appConfig.AI.ContextManagement.ToolResultStorage = new ToolResultStorageConfig
        {
            PerResultCharLimit = 100,
            PreviewSizeChars = 20,
            StoragePath = _tempDir
        };

        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(_appConfig);

        _sut = new FileSystemToolResultStore(
            monitor.Object,
            Mock.Of<ILogger<FileSystemToolResultStore>>());
    }

    [Fact]
    public async Task StoreIfLargeAsync_SmallResult_ReturnsInlineWithFullContent()
    {
        var output = "small output";

        var result = await _sut.StoreIfLargeAsync("session1", "read_file", null, output);

        result.PreviewContent.Should().Be(output);
        result.FullContentPath.Should().BeNull();
        result.IsPersistedToDisk.Should().BeFalse();
        result.SizeChars.Should().Be(output.Length);
    }

    [Fact]
    public async Task StoreIfLargeAsync_LargeResult_PersistsToDiskWithPreview()
    {
        var output = new string('x', 200);

        var result = await _sut.StoreIfLargeAsync("session1", "search", null, output);

        result.IsPersistedToDisk.Should().BeTrue();
        result.FullContentPath.Should().NotBeNullOrWhiteSpace();
        result.SizeChars.Should().Be(200);
        result.PreviewContent.Should().StartWith(new string('x', 20));
    }

    [Fact]
    public async Task StoreAndRetrieve_RoundTrips()
    {
        var output = new string('a', 200);
        var stored = await _sut.StoreIfLargeAsync("session1", "tool", null, output);

        var retrieved = await _sut.RetrieveFullContentAsync(stored.ResultId);

        retrieved.Should().Be(output);
    }

    [Fact]
    public async Task RetrieveFullContentAsync_MissingId_ThrowsKeyNotFoundException()
    {
        var act = () => _sut.RetrieveFullContentAsync("nonexistent-id");

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task StoreIfLargeAsync_PathTraversalInSessionId_ThrowsArgumentException()
    {
        var act = () => _sut.StoreIfLargeAsync("../escape", "tool", null, "data");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("sessionId");
    }

    [Fact]
    public async Task StoreIfLargeAsync_NullOutput_ThrowsArgumentNullException()
    {
        var act = () => _sut.StoreIfLargeAsync("session1", "tool", null, null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task StoreIfLargeAsync_EmptySessionId_ThrowsArgumentException()
    {
        var act = () => _sut.StoreIfLargeAsync("", "tool", null, "data");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup in test
        }
    }
}
