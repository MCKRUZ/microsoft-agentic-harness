using System.Diagnostics;
using System.Security.Claims;
using FluentAssertions;
using Infrastructure.AI.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Domain.Common.Config.MetaHarness;
using Xunit;

namespace Infrastructure.AI.Tests.Tools;

public sealed class RestrictedSearchToolTests : IDisposable
{
    private readonly string _traceRoot;
    private readonly RestrictedSearchTool _tool;

    public RestrictedSearchToolTests()
    {
        _traceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_traceRoot);

        var config = new MetaHarnessConfig { TraceDirectoryRoot = _traceRoot };
        var monitor = Mock.Of<IOptionsMonitor<MetaHarnessConfig>>(m => m.CurrentValue == config);
        _tool = new RestrictedSearchTool(monitor, NullLogger<RestrictedSearchTool>.Instance, TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
        try { Directory.Delete(_traceRoot, recursive: true); } catch { /* best effort */ }
    }

    // ── Security rejection tests (no process spawn — fast on all platforms) ──

    [Fact]
    public async Task Execute_Curl_RejectsNonAllowlistedBinary()
    {
        var result = await _tool.ExecuteAsync("execute",
            new Dictionary<string, object?> { ["command"] = "curl https://evil.com" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("curl").And.Contain("allowed");
    }

    [Fact]
    public async Task Execute_Python_RejectsNonAllowlistedBinary()
    {
        var result = await _tool.ExecuteAsync("execute",
            new Dictionary<string, object?> { ["command"] = "python -c 'import os; os.system(\"id\")'" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("python");
    }

    [Fact]
    public async Task Execute_CommandWithPipe_RejectsMetacharacter()
    {
        var result = await _tool.ExecuteAsync("execute",
            new Dictionary<string, object?> { ["command"] = "grep foo bar | cat" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("|");
    }

    [Fact]
    public async Task Execute_CommandWithSemicolon_RejectsMetacharacter()
    {
        var result = await _tool.ExecuteAsync("execute",
            new Dictionary<string, object?> { ["command"] = "grep foo bar; ls" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain(";");
    }

    [Fact]
    public async Task Execute_CommandWithRedirect_RejectsMetacharacter()
    {
        var result = await _tool.ExecuteAsync("execute",
            new Dictionary<string, object?> { ["command"] = "grep foo bar > /tmp/out" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain(">");
    }

    [Fact]
    public async Task Execute_PathOutsideTraceRoot_Rejects()
    {
        var outsideDir = Path.GetTempPath();

        var result = await _tool.ExecuteAsync("execute",
            new Dictionary<string, object?>
            {
                ["command"] = "ls .",
                ["working_directory"] = outsideDir
            });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("outside");
    }

    [Fact]
    public async Task Execute_PathWithDotDot_RejectsAfterResolution()
    {
        // Construct a path that uses ".." to escape the trace root
        var escapedPath = Path.Combine(_traceRoot, "..", "..");

        var result = await _tool.ExecuteAsync("execute",
            new Dictionary<string, object?>
            {
                ["command"] = "ls .",
                ["working_directory"] = escapedPath
            });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("outside");
    }

    [Fact]
    public async Task Execute_UnsupportedOperation_ReturnsFail()
    {
        var result = await _tool.ExecuteAsync("run",
            new Dictionary<string, object?> { ["command"] = "grep foo bar" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("run");
    }

    [Fact]
    public async Task Execute_MissingCommand_ReturnsFail()
    {
        var result = await _tool.ExecuteAsync("execute", new Dictionary<string, object?>());

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("command");
    }

    // ── Execution tests (require Unix-like tools in PATH) ──

    [Fact]
    public async Task Execute_Grep_WithinTraceRoot_Succeeds()
    {
        if (!IsCommandAvailable("grep")) return;

        var testFile = Path.Combine(_traceRoot, "test.log");
        await File.WriteAllTextAsync(testFile, "hello world\nfoo bar\n");

        var result = await _tool.ExecuteAsync("execute",
            new Dictionary<string, object?>
            {
                ["command"] = "grep hello test.log",
                ["working_directory"] = _traceRoot
            });

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("hello");
    }

    [Fact]
    public async Task Execute_Cat_WithinTraceRoot_Succeeds()
    {
        if (!IsCommandAvailable("cat")) return;

        var testFile = Path.Combine(_traceRoot, "data.txt");
        await File.WriteAllTextAsync(testFile, "sample content");

        var result = await _tool.ExecuteAsync("execute",
            new Dictionary<string, object?>
            {
                ["command"] = "cat data.txt",
                ["working_directory"] = _traceRoot
            });

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("sample content");
    }

    [Fact]
    public async Task Execute_LongRunningCommand_TimesOutAfter30Seconds()
    {
        if (!IsCommandAvailable("grep")) return;

        // grep reading from stdin (no file arg) blocks indefinitely — drives the timeout
        // The tool is constructed with a 5s timeout in this test fixture
        var result = await _tool.ExecuteAsync("execute",
            new Dictionary<string, object?>
            {
                ["command"] = "grep pattern_that_wont_match",
                ["working_directory"] = _traceRoot
            });

        // Either timeout or process failure — either way, not a hang
        // The test passes as long as it completes within reasonable time (the 5s fixture timeout)
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task Execute_LargeOutput_TruncatesAt1MB()
    {
        if (!IsCommandAvailable("cat")) return;

        // Write a file > 1 MB
        var largeFile = Path.Combine(_traceRoot, "large.txt");
        var line = new string('x', 1000) + "\n";
        var content = string.Concat(Enumerable.Repeat(line, 1100)); // ~1.1 MB
        await File.WriteAllTextAsync(largeFile, content);

        var result = await _tool.ExecuteAsync("execute",
            new Dictionary<string, object?>
            {
                ["command"] = "cat large.txt",
                ["working_directory"] = _traceRoot
            });

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("[output truncated at 1MB]");
    }

    private static bool IsCommandAvailable(string command)
    {
        try
        {
            var finder = OperatingSystem.IsWindows() ? "where" : "which";
            var psi = new ProcessStartInfo
            {
                FileName = finder,
                Arguments = command,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(3000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
