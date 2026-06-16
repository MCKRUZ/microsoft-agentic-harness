using Domain.Common.Config;
using Domain.Common.Config.AI.ContextManagement;
using FluentAssertions;
using Infrastructure.AI.Context;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Context;

/// <summary>
/// Regression tests for the solution-review path-traversal hardening of
/// <see cref="FileSystemToolResultStore"/>. The prior guard compared
/// <c>sessionId != Path.GetFileName(sessionId)</c> only, which let bare relative
/// directory references (".", "..") through because <see cref="Path.GetFileName(string)"/>
/// preserves them verbatim — allowing <see cref="Path.Combine(string, string)"/> to escape
/// the configured storage root.
/// </summary>
public sealed class FileSystemToolResultStoreSolutionReviewFixTests : IDisposable
{
    private readonly FileSystemToolResultStore _sut;
    private readonly string _tempDir;

    public FileSystemToolResultStoreSolutionReviewFixTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "toolresult-fix-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var appConfig = new AppConfig();
        appConfig.AI.ContextManagement.ToolResultStorage = new ToolResultStorageConfig
        {
            PerResultCharLimit = 100,
            PreviewSizeChars = 20,
            StoragePath = _tempDir
        };

        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(appConfig);

        _sut = new FileSystemToolResultStore(
            monitor.Object,
            Mock.Of<ILogger<FileSystemToolResultStore>>());
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    public async Task StoreIfLargeAsync_RelativeDirectoryReferenceSessionId_ThrowsArgumentException(string sessionId)
    {
        // A large output forces the disk-write path where the traversal would occur.
        var largeOutput = new string('x', 200);

        var act = () => _sut.StoreIfLargeAsync(sessionId, "tool", null, largeOutput);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("sessionId");
    }

    [Theory]
    [InlineData("..\\escape")]
    [InlineData("nested/segment")]
    [InlineData("nested\\segment")]
    public async Task StoreIfLargeAsync_SeparatorInSessionId_ThrowsArgumentException(string sessionId)
    {
        var largeOutput = new string('x', 200);

        var act = () => _sut.StoreIfLargeAsync(sessionId, "tool", null, largeOutput);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("sessionId");
    }

    [Fact]
    public async Task StoreIfLargeAsync_RejectedTraversalSessionId_DoesNotEscapeStorageRoot()
    {
        var largeOutput = new string('x', 200);

        try
        {
            await _sut.StoreIfLargeAsync("..", "tool", null, largeOutput);
        }
        catch (ArgumentException)
        {
            // Expected — the guard rejects the traversal attempt.
        }

        // The parent of the storage root must not have gained a "tool-results" directory,
        // which is what a successful "../tool-results" combine would have created.
        var parent = Directory.GetParent(_tempDir)!.FullName;
        Directory.Exists(Path.Combine(parent, "tool-results")).Should().BeFalse();
    }

    [Fact]
    public async Task StoreIfLargeAsync_LegitimateSessionId_PersistsUnderStorageRoot()
    {
        var largeOutput = new string('y', 200);

        var result = await _sut.StoreIfLargeAsync("session-abc_123", "tool", null, largeOutput);

        result.FullContentPath.Should().NotBeNullOrWhiteSpace();
        Path.GetFullPath(result.FullContentPath!)
            .Should().StartWith(Path.GetFullPath(_tempDir));
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
            // Best-effort cleanup in test.
        }
    }
}
