using Application.AI.Common.Interfaces.Workspace;
using FluentAssertions;
using Infrastructure.AI.Tests.Tools.Workspace.Support;
using Infrastructure.AI.Tools.Workspace;
using Xunit;

namespace Infrastructure.AI.Tests.Tools.Workspace;

/// <summary>
/// Unit tests for <see cref="WorkspaceReadFileTool"/>. Verifies the tool reads
/// from the ambient sandbox-injected workspace, rejects path-escape attempts,
/// and refuses to run when no workspace scope is active.
/// </summary>
public sealed class WorkspaceReadFileToolTests
{
    [Fact]
    public async Task Read_ReturnsFileContentsFromSandboxInjectedWorkspace()
    {
        using var fx = new WorkspaceTestFixture();
        fx.WriteFile("hello.txt", "hello-from-workspace");
        var sut = new WorkspaceReadFileTool(fx.Accessor);

        var result = await sut.ExecuteAsync(
            "read",
            new Dictionary<string, object?> { ["path"] = "hello.txt" });

        result.Success.Should().BeTrue();
        result.Output.Should().Be("hello-from-workspace");
    }

    [Fact]
    public async Task Read_NoWorkspaceScope_Refuses()
    {
        // Accessor with no active scope — simulates a tool call outside the sandbox harness.
        var bareAccessor = new WorkspaceContextAccessor();
        var sut = new WorkspaceReadFileTool(bareAccessor);

        var result = await sut.ExecuteAsync(
            "read",
            new Dictionary<string, object?> { ["path"] = "anything.txt" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("workspace context is active", "the tool must fail loud when the ambient is unset");
    }

    [Fact]
    public async Task Read_PathEscape_DeniesWithoutLeakingHostPaths()
    {
        using var fx = new WorkspaceTestFixture();
        var sut = new WorkspaceReadFileTool(fx.Accessor);

        var result = await sut.ExecuteAsync(
            "read",
            new Dictionary<string, object?> { ["path"] = "../escape-attempt.txt" });

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Access denied: path is outside the workspace.");
    }

    [Fact]
    public async Task Read_MissingPathParameter_ReturnsFailure()
    {
        using var fx = new WorkspaceTestFixture();
        var sut = new WorkspaceReadFileTool(fx.Accessor);

        var result = await sut.ExecuteAsync("read", new Dictionary<string, object?>());

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("'path' is missing");
    }

    [Fact]
    public async Task Read_UnknownOperation_ReturnsFailure()
    {
        using var fx = new WorkspaceTestFixture();
        var sut = new WorkspaceReadFileTool(fx.Accessor);

        var result = await sut.ExecuteAsync(
            "delete",
            new Dictionary<string, object?> { ["path"] = "x" });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Unknown operation");
    }

    [Fact]
    public void Tool_IsReadOnlyAndConcurrencySafe()
    {
        using var fx = new WorkspaceTestFixture();
        var sut = new WorkspaceReadFileTool(fx.Accessor);

        sut.IsReadOnly.Should().BeTrue();
        sut.IsConcurrencySafe.Should().BeTrue();
        sut.Name.Should().Be("read_file");
    }
}
