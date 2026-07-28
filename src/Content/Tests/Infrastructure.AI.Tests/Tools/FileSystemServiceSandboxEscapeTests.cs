using FluentAssertions;
using Infrastructure.AI.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Tools;

/// <summary>
/// Covers <see cref="FileSystemService"/>'s allowlist arm against links that point out of the
/// sandbox — the recursive-search path specifically.
/// </summary>
/// <remarks>
/// <para>
/// The direct-read path resolves links before comparing against the allowlist. The search walk did
/// not: it canonicalized a path only to answer "is this protected?", then compared the raw
/// enumerated path against the allowlist. A junction at <c>workspace\notes</c> pointing at
/// <c>C:\Users\victim\.ssh</c> is not protected, so the walk descended into it, and every file it
/// then yielded carried a literal <c>workspace\notes\...</c> path that satisfied a
/// workspace-rooted allowlist — after which <see cref="File.ReadLinesAsync(string)"/> followed the
/// link and put the contents in the search results.
/// </para>
/// <para>
/// This is the more reachable of the two escapes the branch was reviewed for: <c>mklink /J</c>
/// needs no privilege on Windows, and git can carry symlinks into a cloned workspace, so an
/// attacker does not need a foothold beyond the workspace the agent is already pointed at.
/// </para>
/// </remarks>
public sealed class FileSystemServiceSandboxEscapeTests : IDisposable
{
    private const string Secret = "needle-private-key-material";

    private readonly string _sandboxRoot;
    private readonly string _outsideRoot;
    private readonly FileSystemService _sut;

    public FileSystemServiceSandboxEscapeTests()
    {
        var scope = Path.Combine(Path.GetTempPath(), $"fss-esc-{Guid.NewGuid():N}");
        _sandboxRoot = Path.Combine(scope, "workspace");
        _outsideRoot = Path.Combine(scope, "victim");

        Directory.CreateDirectory(_sandboxRoot);
        Directory.CreateDirectory(_outsideRoot);
        File.WriteAllText(Path.Combine(_outsideRoot, "id_rsa"), Secret);

        // Only the workspace is allowed. "victim" is a sibling, exactly as a user's home directory
        // is a sibling of the agent's sandbox — reachable on the same volume, never allowlisted.
        _sut = new FileSystemService(
            NullLogger<FileSystemService>.Instance,
            [_sandboxRoot]);
    }

    public void Dispose()
    {
        var scope = Path.GetDirectoryName(_sandboxRoot);
        if (scope is not null && Directory.Exists(scope))
            Directory.Delete(scope, recursive: true);
    }

    [SkippableFact]
    public async Task SearchFilesAsync_DirectoryLinkPointingOutsideTheSandbox_LeaksNothing()
    {
        SandboxLinkFactory.CreateDirectoryLink(Path.Combine(_sandboxRoot, "notes"), _outsideRoot);

        var results = await _sut.SearchFilesAsync(_sandboxRoot, "needle");

        results.Should().BeEmpty(
            "the walk must prune a subdirectory whose canonical target is outside every allowed " +
            "base path, however workspace-shaped its literal path looks");
    }

    [SkippableFact]
    public async Task SearchFilesAsync_FileLinkPointingOutsideTheSandbox_LeaksNothing()
    {
        // The per-file gate, not the directory prune: the link sits directly in an allowed
        // directory whose verdict has already been recorded, which is exactly the case the
        // parent-verdict memo is tempted to wave through.
        SandboxLinkFactory.CreateFileLink(Path.Combine(_sandboxRoot, "innocent.txt"),
            Path.Combine(_outsideRoot, "id_rsa"));

        var results = await _sut.SearchFilesAsync(_sandboxRoot, "needle");

        results.Should().BeEmpty(
            "a file link is resolved before the allowlist comparison, so it cannot read a file " +
            "outside the sandbox through an allowed directory");
    }

    [SkippableFact]
    public async Task SearchFilesAsync_NestedFileBeneathADirectoryLink_LeaksNothing()
    {
        // The deepest form: the file is neither the link nor a direct child of an allowed
        // directory. Its parent's verdict is inherited from the memo, so the inherited entry has
        // to carry the parent's CANONICAL path — inheriting only "not protected" leaves the
        // allowlist comparing the literal workspace-shaped path and the file leaks.
        Directory.CreateDirectory(Path.Combine(_outsideRoot, "nested"));
        await File.WriteAllTextAsync(Path.Combine(_outsideRoot, "nested", "deep.txt"), Secret);

        SandboxLinkFactory.CreateDirectoryLink(Path.Combine(_sandboxRoot, "notes"), _outsideRoot);

        var results = await _sut.SearchFilesAsync(_sandboxRoot, "needle");

        results.Should().BeEmpty();
    }

    [SkippableFact]
    public async Task ReadFileAsync_ThroughADirectoryLinkPointingOutsideTheSandbox_IsDenied()
    {
        // Parity check for the claim the search gate documents about itself: the direct-read path
        // must reach the same verdict on the same path. If this ever diverges from the search
        // results above, one of the two gates has drifted.
        SandboxLinkFactory.CreateDirectoryLink(Path.Combine(_sandboxRoot, "notes"), _outsideRoot);

        var act = async () => await _sut.ReadFileAsync(Path.Combine(_sandboxRoot, "notes", "id_rsa"));

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task SearchFilesAsync_OrdinaryFileInsideTheSandbox_IsStillFound()
    {
        // The escape fix prunes on the canonical path, which is also the path an ordinary file is
        // now judged by. If canonicalization ever stopped agreeing with the configured base paths,
        // the tool would deny everything and every test above would pass vacuously.
        await File.WriteAllTextAsync(Path.Combine(_sandboxRoot, "notes.txt"), "needle-in-workspace");

        var results = await _sut.SearchFilesAsync(_sandboxRoot, "needle");

        results.Should().ContainSingle();
        results[0].FilePath.Should().Contain("notes.txt");
    }
}
