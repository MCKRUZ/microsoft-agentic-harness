using System.Security.Claims;
using Domain.AI.MCP;
using Domain.Common.Config.MetaHarness;
using FluentAssertions;
using Infrastructure.AI.MCP.Resources;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.MCP;

public sealed class TraceResourceProviderTests : IDisposable
{
    private readonly string _traceRoot;
    private readonly string _runId;
    private readonly string _runDir;
    private readonly TraceResourceProvider _provider;
    private readonly McpRequestContext _authContext;

    public TraceResourceProviderTests()
    {
        _traceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _runId = "run-" + Guid.NewGuid().ToString("N")[..8];
        _runDir = Path.Combine(_traceRoot, "optimizations", _runId);
        Directory.CreateDirectory(_runDir);

        var config = new MetaHarnessConfig
        {
            TraceDirectoryRoot = _traceRoot,
            EnableMcpTraceResources = true
        };
        var monitor = Mock.Of<IOptionsMonitor<MetaHarnessConfig>>(m => m.CurrentValue == config);
        _provider = new TraceResourceProvider(monitor, NullLogger<TraceResourceProvider>.Instance);

        // Authenticated context: non-empty AuthenticationType makes IsAuthenticated = true
        var identity = new ClaimsIdentity("Bearer");
        _authContext = McpRequestContext.FromPrincipal(new ClaimsPrincipal(identity));
    }

    public void Dispose()
    {
        try { Directory.Delete(_traceRoot, recursive: true); } catch { /* best effort */ }
    }

    // ── Auth tests ──

    [Fact]
    public async Task Read_WithoutAuth_Rejects()
    {
        var file = Path.Combine(_runDir, "output.json");
        await File.WriteAllTextAsync(file, "{}");

        var act = () => _provider.ReadAsync(
            $"trace://{_runId}/output.json",
            McpRequestContext.Unauthenticated,
            CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task List_WithoutAuth_Rejects()
    {
        var act = () => _provider.ListAsync(
            $"trace://{_runId}/",
            McpRequestContext.Unauthenticated,
            CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    // ── List tests ──

    [Fact]
    public async Task List_ValidOptimizationRunId_ReturnsFiles()
    {
        await File.WriteAllTextAsync(Path.Combine(_runDir, "manifest.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(_runDir, "summary.txt"), "ok");

        var resources = await _provider.ListAsync(
            $"trace://{_runId}/", _authContext, CancellationToken.None);

        resources.Should().HaveCount(2);
        resources.Select(r => r.Uri).Should().AllSatisfy(u => u.Should().StartWith("trace://"));
    }

    // ── Read tests ──

    [Fact]
    public async Task Read_ValidPath_ReturnsFileContent()
    {
        var file = Path.Combine(_runDir, "result.json");
        await File.WriteAllTextAsync(file, "{\"score\": 0.9}");

        var content = await _provider.ReadAsync(
            $"trace://{_runId}/result.json", _authContext, CancellationToken.None);

        content.Uri.Should().Be($"trace://{_runId}/result.json");
        content.Text.Should().Be("{\"score\": 0.9}");
    }

    [Fact]
    public async Task Read_PathWithDotDot_RejectsTraversal()
    {
        var act = () => _provider.ReadAsync(
            $"trace://{_runId}/../../../etc/passwd",
            _authContext,
            CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task Read_PathOutsideOptimizationRunDir_Rejects()
    {
        // Encode traversal without literal ".." — resolved path escapes run dir
        var act = () => _provider.ReadAsync(
            $"trace://{_runId}/subdir/../../other-run/secret.txt",
            _authContext,
            CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task Read_SymlinkOutsideRoot_Rejects()
    {
        if (OperatingSystem.IsWindows()) return; // symlinks require elevated privileges on Windows

        var outsideFile = Path.Combine(Path.GetTempPath(), "outside-" + Guid.NewGuid() + ".txt");
        await File.WriteAllTextAsync(outsideFile, "secret");

        var symlinkPath = Path.Combine(_runDir, "link.txt");
        File.CreateSymbolicLink(symlinkPath, outsideFile);

        try
        {
            var act = () => _provider.ReadAsync(
                $"trace://{_runId}/link.txt", _authContext, CancellationToken.None);

            await act.Should().ThrowAsync<UnauthorizedAccessException>();
        }
        finally
        {
            try { File.Delete(outsideFile); } catch { }
        }
    }
}
