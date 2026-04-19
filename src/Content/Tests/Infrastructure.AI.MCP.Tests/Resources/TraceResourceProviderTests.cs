using System.Security.Claims;
using Domain.AI.MCP;
using Domain.Common.Config.MetaHarness;
using FluentAssertions;
using Infrastructure.AI.MCP.Resources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.MCP.Tests.Resources;

/// <summary>
/// Tests for <see cref="TraceResourceProvider"/> covering auth gating,
/// feature flag behavior, URI parsing, path traversal protection, and
/// file I/O for listing and reading trace resources.
/// </summary>
public sealed class TraceResourceProviderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Mock<IOptionsMonitor<MetaHarnessConfig>> _configMock;
    private readonly TraceResourceProvider _sut;

    public TraceResourceProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"trace-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var config = new MetaHarnessConfig
        {
            TraceDirectoryRoot = _tempDir,
            EnableMcpTraceResources = true
        };

        _configMock = new Mock<IOptionsMonitor<MetaHarnessConfig>>();
        _configMock.Setup(m => m.CurrentValue).Returns(config);

        _sut = new TraceResourceProvider(
            _configMock.Object,
            Mock.Of<ILogger<TraceResourceProvider>>());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static McpRequestContext AuthenticatedContext()
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "test-user")], "TestAuth");
        return McpRequestContext.FromPrincipal(new ClaimsPrincipal(identity));
    }

    private string CreateRunFile(string runId, string relativePath, string content = "test")
    {
        var dir = Path.Combine(_tempDir, "optimizations", runId);
        var fullPath = Path.Combine(dir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    // -- Constructor --

    [Fact]
    public void Constructor_NullConfig_ThrowsArgumentNullException()
    {
        var act = () => new TraceResourceProvider(
            null!, Mock.Of<ILogger<TraceResourceProvider>>());

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        var act = () => new TraceResourceProvider(
            _configMock.Object, null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // -- ListAsync auth --

    [Fact]
    public async Task ListAsync_UnauthenticatedContext_ThrowsUnauthorizedAccessException()
    {
        var act = () => _sut.ListAsync("trace://run-1", McpRequestContext.Unauthenticated, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    // -- ListAsync feature flag --

    [Fact]
    public async Task ListAsync_FeatureDisabled_ReturnsEmpty()
    {
        var config = new MetaHarnessConfig
        {
            TraceDirectoryRoot = _tempDir,
            EnableMcpTraceResources = false
        };
        _configMock.Setup(m => m.CurrentValue).Returns(config);

        var result = await _sut.ListAsync("trace://run-1", AuthenticatedContext(), CancellationToken.None);

        result.Should().BeEmpty();
    }

    // -- ListAsync URI parsing --

    [Fact]
    public async Task ListAsync_InvalidScheme_ReturnsEmpty()
    {
        var result = await _sut.ListAsync("http://run-1", AuthenticatedContext(), CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAsync_EmptyUri_ReturnsEmpty()
    {
        var result = await _sut.ListAsync("trace://", AuthenticatedContext(), CancellationToken.None);

        result.Should().BeEmpty();
    }

    // -- ListAsync with files --

    [Fact]
    public async Task ListAsync_NonexistentRunDirectory_ReturnsEmpty()
    {
        var result = await _sut.ListAsync("trace://nonexistent-run", AuthenticatedContext(), CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAsync_ExistingRun_ReturnsResourcesWithCorrectUris()
    {
        CreateRunFile("run-1", "eval/output.json", "{}");
        CreateRunFile("run-1", "summary.txt", "done");

        var result = await _sut.ListAsync("trace://run-1", AuthenticatedContext(), CancellationToken.None);

        result.Should().HaveCount(2);
        result.Select(r => r.Uri).Should().Contain("trace://run-1/eval/output.json");
        result.Select(r => r.Uri).Should().Contain("trace://run-1/summary.txt");
    }

    [Fact]
    public async Task ListAsync_ResourceNamesAreFileNames()
    {
        CreateRunFile("run-2", "deep/nested/file.txt");

        var result = await _sut.ListAsync("trace://run-2", AuthenticatedContext(), CancellationToken.None);

        result.Should().ContainSingle();
        result[0].Name.Should().Be("file.txt");
    }

    // -- ReadAsync auth --

    [Fact]
    public async Task ReadAsync_UnauthenticatedContext_ThrowsUnauthorizedAccessException()
    {
        var act = () => _sut.ReadAsync("trace://run-1/file.txt", McpRequestContext.Unauthenticated, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    // -- ReadAsync feature flag --

    [Fact]
    public async Task ReadAsync_FeatureDisabled_ThrowsFileNotFoundException()
    {
        var config = new MetaHarnessConfig
        {
            TraceDirectoryRoot = _tempDir,
            EnableMcpTraceResources = false
        };
        _configMock.Setup(m => m.CurrentValue).Returns(config);

        var act = () => _sut.ReadAsync("trace://run-1/file.txt", AuthenticatedContext(), CancellationToken.None);

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    // -- ReadAsync URI parsing --

    [Fact]
    public async Task ReadAsync_InvalidScheme_ThrowsArgumentException()
    {
        var act = () => _sut.ReadAsync("http://run-1/file.txt", AuthenticatedContext(), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ReadAsync_NoPathComponent_ThrowsArgumentException()
    {
        var act = () => _sut.ReadAsync("trace://run-1", AuthenticatedContext(), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // -- ReadAsync path traversal --

    [Fact]
    public async Task ReadAsync_PathTraversal_ThrowsUnauthorizedAccessException()
    {
        var act = () => _sut.ReadAsync("trace://run-1/../../../etc/passwd", AuthenticatedContext(), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*Path traversal*");
    }

    // -- ReadAsync file not found --

    [Fact]
    public async Task ReadAsync_FileDoesNotExist_ThrowsFileNotFoundException()
    {
        // Create run dir but not the file
        CreateRunFile("run-3", "placeholder.txt");

        var act = () => _sut.ReadAsync("trace://run-3/missing.txt", AuthenticatedContext(), CancellationToken.None);

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    // -- ReadAsync success --

    [Fact]
    public async Task ReadAsync_ValidFile_ReturnsContentWithCorrectUri()
    {
        CreateRunFile("run-4", "output.json", "{\"result\": true}");

        var result = await _sut.ReadAsync("trace://run-4/output.json", AuthenticatedContext(), CancellationToken.None);

        result.Uri.Should().Be("trace://run-4/output.json");
        result.Text.Should().Be("{\"result\": true}");
    }

    [Fact]
    public async Task ReadAsync_NestedFile_ReturnsContent()
    {
        CreateRunFile("run-5", "eval/task-1/output.json", "nested content");

        var result = await _sut.ReadAsync("trace://run-5/eval/task-1/output.json", AuthenticatedContext(), CancellationToken.None);

        result.Text.Should().Be("nested content");
    }
}
