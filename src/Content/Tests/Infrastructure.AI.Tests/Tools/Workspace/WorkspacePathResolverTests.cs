using Domain.AI.Workspace;
using FluentAssertions;
using Infrastructure.AI.Tools.Workspace;
using Xunit;

namespace Infrastructure.AI.Tests.Tools.Workspace;

/// <summary>
/// Path-escape regression tests for <see cref="WorkspacePathResolver"/>. The
/// resolver is the single chokepoint every workspace tool routes through, so
/// it gets the bulk of the boundary-condition coverage.
/// </summary>
public sealed class WorkspacePathResolverTests : IDisposable
{
    private readonly string _root;
    private readonly WorkspaceContext _workspace;

    public WorkspacePathResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wpres-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _workspace = new WorkspaceContext(
            workingCopyPath: _root,
            repoUrl: "https://github.com/org/repo",
            branch: "main");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void Resolve_RelativePath_ReturnsAbsoluteInsideWorkingCopy()
    {
        var resolved = WorkspacePathResolver.Resolve(_workspace, "src/file.cs");

        resolved.Should().StartWith(Path.GetFullPath(_root));
        resolved.Should().EndWith("file.cs");
    }

    [Fact]
    public void Resolve_DotDotEscape_ThrowsUnauthorized()
    {
        var attempt = () => WorkspacePathResolver.Resolve(_workspace, "../outside.txt");

        attempt.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Resolve_DeeplyNestedDotDotEscape_ThrowsUnauthorized()
    {
        var attempt = () => WorkspacePathResolver.Resolve(_workspace, "a/b/c/../../../../etc/passwd");

        attempt.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Resolve_AbsolutePathOutsideWorkingCopy_ThrowsUnauthorized()
    {
        var outside = Path.GetTempPath(); // Always above the per-test temp working copy.

        var attempt = () => WorkspacePathResolver.Resolve(_workspace, outside);

        attempt.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Resolve_EmptyOrWhitespace_ThrowsArgumentException()
    {
        FluentActions.Invoking(() => WorkspacePathResolver.Resolve(_workspace, ""))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => WorkspacePathResolver.Resolve(_workspace, "   "))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ToRelative_RoundTrip_NormalisesToForwardSlashes()
    {
        var resolved = WorkspacePathResolver.Resolve(_workspace, "src/sub/file.cs");

        var rel = WorkspacePathResolver.ToRelative(_workspace, resolved);

        rel.Should().Be("src/sub/file.cs");
        rel.Should().NotContain("\\");
    }
}
