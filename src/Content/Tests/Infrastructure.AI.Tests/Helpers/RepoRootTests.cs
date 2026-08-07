using FluentAssertions;
using Tests.Common;
using Xunit;

namespace Infrastructure.AI.Tests.Helpers;

/// <summary>
/// Tests for <see cref="RepoRoot"/>, the shared repository-root finder.
/// </summary>
/// <remarks>
/// <para>
/// Issue #293. Five test helpers each grew their own copy of this walk and four looked for a
/// <c>.git</c> <em>directory</em>. In a git worktree <c>.git</c> is a <em>file</em> holding a
/// <c>gitdir:</c> pointer, so the walk ran off the top of the drive and threw — from a static
/// initializer, which takes the whole test class with it. 19 tests across four classes.
/// </para>
/// <para>
/// <strong><see cref="Find_WorktreeShapeWhereGitIsAFile_LocatesTheRoot"/> is the regression
/// test.</strong> It builds the exact directory shape a worktree has and would fail against any
/// of the four implementations this helper replaced. The others pin the behaviour that shape
/// depends on.
/// </para>
/// </remarks>
public sealed class RepoRootTests : IDisposable
{
    private readonly string _tempRoot;

    public RepoRootTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "reporoot-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public void Find_WorktreeShapeWhereGitIsAFile_LocatesTheRoot()
    {
        // THE regression test. This is precisely what a worktree looks like on disk: .git is a
        // FILE containing a gitdir: pointer, not a directory. Every implementation this helper
        // replaced walked past this root and threw.
        var root = CreateRepoLikeRoot();
        File.WriteAllText(Path.Combine(root, ".git"), "gitdir: C:/somewhere/.git/worktrees/wt1");

        var start = Path.Combine(root, "src", "Content", "Tests", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(start);

        RepoRoot.Find(start).Should().Be(root,
            "a worktree is the arrangement this repo's own gate-runner guidance recommends");
    }

    [Fact]
    public void Find_MainCheckoutShapeWhereGitIsADirectory_LocatesTheSameRoot()
    {
        // The control: the arrangement that always worked must keep working. Anchoring on the
        // solution file rather than on .git means both shapes resolve identically.
        var root = CreateRepoLikeRoot();
        Directory.CreateDirectory(Path.Combine(root, ".git"));

        var start = Path.Combine(root, "src", "Content", "Tests", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(start);

        RepoRoot.Find(start).Should().Be(root);
    }

    [Fact]
    public void Find_NoGitEntryAtAll_StillLocatesTheRoot()
    {
        // A source archive, a CI checkout that stripped .git, a container COPY of the tree: none
        // of these have a .git entry of any kind, and none of them should stop a test reading a
        // file that is checked in beside the code.
        var root = CreateRepoLikeRoot();

        var start = Path.Combine(root, "src", "Content");

        RepoRoot.Find(start).Should().Be(root);
    }

    [Fact]
    public void Find_StartingAtTheRootItself_ReturnsIt()
    {
        var root = CreateRepoLikeRoot();

        RepoRoot.Find(root).Should().Be(root);
    }

    [Fact]
    public void Find_NoAnchorInAnyAncestor_ThrowsNamingTheAnchorAndTheStartingPoint()
    {
        // The failure surfaces from a static initializer, where the stack trace says almost
        // nothing about what was being looked for. The message has to carry that itself.
        var orphan = Path.Combine(_tempRoot, "no-repo-here", "deep");
        Directory.CreateDirectory(orphan);

        var act = () => RepoRoot.Find(orphan);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should()
                .Contain("src/AgenticHarness.slnx", "the message must name what was missing")
                .And.Contain(orphan, "and where the search began");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Find_BlankStartDirectory_Throws(string blank)
    {
        var act = () => RepoRoot.Find(blank);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Path_ResolvesTheRealRepository()
    {
        // Not a tautology: it proves the anchor this helper looks for actually exists in the
        // checkout the tests are running from, which is what every caller depends on.
        File.Exists(System.IO.Path.Combine(RepoRoot.Path, "src", "AgenticHarness.slnx"))
            .Should().BeTrue();
    }

    [Fact]
    public void Combine_AppendsSegmentsBelowTheRoot()
    {
        RepoRoot.Combine("scripts", "otel-collector", "config.yaml")
            .Should().Be(System.IO.Path.Combine(
                RepoRoot.Path, "scripts", "otel-collector", "config.yaml"));
    }

    /// <summary>Creates a directory tree carrying the anchor file, and returns its root.</summary>
    private string CreateRepoLikeRoot()
    {
        var root = Path.Combine(_tempRoot, "repo");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "AgenticHarness.slnx"), "<Solution />");
        return root;
    }
}
