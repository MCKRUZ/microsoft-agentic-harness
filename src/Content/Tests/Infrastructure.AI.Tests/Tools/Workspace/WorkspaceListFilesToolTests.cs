using FluentAssertions;
using Infrastructure.AI.Tests.Tools.Workspace.Support;
using Infrastructure.AI.Tools.Workspace;
using Xunit;

namespace Infrastructure.AI.Tests.Tools.Workspace;

/// <summary>
/// Unit tests for <see cref="WorkspaceListFilesTool"/>. Verifies non-recursive
/// listing, glob filtering, recursive listing, and ambient/path-escape refusal.
/// </summary>
public sealed class WorkspaceListFilesToolTests
{
    [Fact]
    public async Task List_ReturnsEntriesRelativeToWorkingCopy()
    {
        using var fx = new WorkspaceTestFixture();
        fx.WriteFile("a.txt", "a");
        fx.WriteFile("b.txt", "b");
        var sut = new WorkspaceListFilesTool(fx.Accessor);

        var result = await sut.ExecuteAsync(
            "list",
            new Dictionary<string, object?> { ["path"] = "." });

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("a.txt").And.Contain("b.txt");
        result.Output.Should().NotContain("\\", "entries must use forward-slash separators");
    }

    [Fact]
    public async Task List_WithGlob_FiltersResults()
    {
        using var fx = new WorkspaceTestFixture();
        fx.WriteFile("a.cs", "x");
        fx.WriteFile("b.md", "y");
        var sut = new WorkspaceListFilesTool(fx.Accessor);

        var result = await sut.ExecuteAsync(
            "list",
            new Dictionary<string, object?>
            {
                ["path"] = ".",
                ["pattern"] = "*.cs"
            });

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("a.cs").And.NotContain("b.md");
    }

    [Fact]
    public async Task List_Recursive_VisitsNestedFolders()
    {
        using var fx = new WorkspaceTestFixture();
        fx.WriteFileInFolder("nested", "deep.txt", "value");
        var sut = new WorkspaceListFilesTool(fx.Accessor);

        var result = await sut.ExecuteAsync(
            "list",
            new Dictionary<string, object?>
            {
                ["path"] = ".",
                ["recursive"] = true
            });

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("nested/deep.txt");
    }

    [Fact]
    public async Task List_NoWorkspaceScope_Refuses()
    {
        var bareAccessor = new WorkspaceContextAccessor();
        var sut = new WorkspaceListFilesTool(bareAccessor);

        var result = await sut.ExecuteAsync(
            "list",
            new Dictionary<string, object?> { ["path"] = "." });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("workspace context is active");
    }

    [Fact]
    public async Task List_EscapeAttempt_Denied()
    {
        using var fx = new WorkspaceTestFixture();
        var sut = new WorkspaceListFilesTool(fx.Accessor);

        var result = await sut.ExecuteAsync(
            "list",
            new Dictionary<string, object?> { ["path"] = ".." });

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Access denied: path is outside the workspace.");
    }
}
