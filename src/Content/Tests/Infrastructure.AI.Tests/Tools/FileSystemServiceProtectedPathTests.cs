using FluentAssertions;
using Infrastructure.AI.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Tools;

/// <summary>
/// Covers <see cref="FileSystemService"/>'s protected-path deny list — the rule that keeps the
/// harness's own governance-state directory unreachable even when it sits inside an allowed base
/// path.
/// </summary>
/// <remarks>
/// The stakes are why these are worth their runtime: the protected directory holds the SQLite
/// database of approval verdicts. An agent that could read it could mine approval payloads; one
/// that could write it could forge a human approval, which the reconciler would then re-drive into
/// the hash-chained compliance audit log. Every test here is a bypass that would achieve one of
/// those, so each must stay closed independently of the others.
/// </remarks>
public sealed class FileSystemServiceProtectedPathTests : IDisposable
{
    private readonly string _root;
    private readonly string _protectedDir;
    private readonly FileSystemService _sut;

    public FileSystemServiceProtectedPathTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"fss-prot-{Guid.NewGuid():N}");
        _protectedDir = Path.Combine(_root, ".agent-state");
        Directory.CreateDirectory(_protectedDir);

        _sut = new FileSystemService(
            NullLogger<FileSystemService>.Instance,
            [_root],
            [_protectedDir]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ReadFileAsync_FileInsideProtectedDirectory_IsDenied()
    {
        var secret = Path.Combine(_protectedDir, "governance-state.db");
        await File.WriteAllTextAsync(secret, "approval-verdicts");

        var act = async () => await _sut.ReadFileAsync(secret);

        await act.Should().ThrowAsync<UnauthorizedAccessException>(
            "the deny list must win over the allowlist the protected directory sits inside");
    }

    [Fact]
    public async Task SearchFilesAsync_DoesNotDescendIntoProtectedDirectory()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_protectedDir, "governance-state.db"), "needle-in-verdicts");
        await File.WriteAllTextAsync(Path.Combine(_root, "ordinary.txt"), "needle-in-workspace");

        var results = await _sut.SearchFilesAsync(_root, "needle");

        results.Should().ContainSingle(
            "the ordinary file matches and the protected one must never be scanned");
        results[0].FilePath.Should().Contain("ordinary.txt");
    }

    [Fact]
    public async Task SearchFilesAsync_SiblingWhoseNameSharesThePrefix_IsStillSearchable()
    {
        // Guards the directory-boundary rule: ".agent-state-docs" is a legitimate sibling, not a
        // child of ".agent-state". A plain string-prefix check would wrongly deny it.
        var sibling = Path.Combine(_root, ".agent-state-docs");
        Directory.CreateDirectory(sibling);
        await File.WriteAllTextAsync(Path.Combine(sibling, "notes.txt"), "needle-in-docs");

        var results = await _sut.SearchFilesAsync(_root, "needle");

        results.Should().ContainSingle("the sibling directory is not protected");
        results[0].FilePath.Should().Contain("notes.txt");
    }

    [Fact]
    public async Task SearchFilesAsync_ProtectedDirectoryNestedBelowAnAllowedParent_IsStillPruned()
    {
        // The per-operation verdict memo must never decide a DIRECTORY's verdict. A protected
        // directory is an ordinary, non-reparse-point directory whose parent is legitimately
        // unprotected, so parent-verdict inheritance — which is sound for files — reports it as
        // allowed, prunes nothing, and the walk reads the approval-verdict database. Nesting it a
        // level down is what puts an already-memoized parent above it and exercises that path.
        var nestedProtected = Path.Combine(_root, "deep", ".agent-state");
        Directory.CreateDirectory(nestedProtected);
        await File.WriteAllTextAsync(
            Path.Combine(nestedProtected, "governance-state.db"), "needle-in-verdicts");

        var sut = new FileSystemService(
            NullLogger<FileSystemService>.Instance, [_root], [nestedProtected]);

        var results = await sut.SearchFilesAsync(_root, "needle");

        results.Should().BeEmpty("a protected directory is pruned wherever it sits in the walk");
    }

    [SkippableFact]
    public async Task SearchFilesAsync_CaseDistinctProtectedDirectories_AreBothDenied()
    {
        Skip.If(
            OperatingSystem.IsWindows(),
            "Windows paths are case-insensitive, so 'state' and 'State' name one directory and the " +
            "de-duplication this test detects cannot arise. It runs on the Linux CI runners.");

        // The deny set must be keyed by PathScope.Comparer, never a hardcoded OrdinalIgnoreCase.
        // On a case-sensitive filesystem these are two different directories; a case-insensitive
        // HashSet collapses them into one entry and the survivor silently stops being protected.
        // That failure direction is fail-OPEN, which is why it belongs in the deny set's tests.
        var lower = Path.Combine(_root, "state");
        var upper = Path.Combine(_root, "State");
        Directory.CreateDirectory(lower);
        Directory.CreateDirectory(upper);
        await File.WriteAllTextAsync(Path.Combine(lower, "a.db"), "needle-lower-verdicts");
        await File.WriteAllTextAsync(Path.Combine(upper, "b.db"), "needle-upper-verdicts");

        var sut = new FileSystemService(
            NullLogger<FileSystemService>.Instance, [_root], [lower, upper]);

        var results = await sut.SearchFilesAsync(_root, "needle");

        results.Should().BeEmpty("both case-distinct protected directories must survive the set");
    }

    [SkippableFact]
    public async Task SearchFilesAsync_SymlinkIntoProtectedDirectory_IsStillDenied()
    {
        // The per-operation directory-verdict memo lets a file inherit its parent directory's
        // verdict, which is what keeps a recursive search from resolving links once per file. This
        // is the case that inheritance must NOT swallow: the parent directory is legitimately
        // unprotected, but the file inside it is a symlink whose target is the governance-state
        // database. Inheriting "allowed" here would publish approval verdicts into search results.
        var target = Path.Combine(_protectedDir, "governance-state.db");
        await File.WriteAllTextAsync(target, "needle-in-verdicts");

        var link = Path.Combine(_root, "innocent.txt");
        TryCreateSymlink(target, link);

        var results = await _sut.SearchFilesAsync(_root, "needle");

        results.Should().BeEmpty(
            "a symlink is resolved to its target before the deny check, so it cannot be used to " +
            "read the protected database through an unprotected directory");
    }

    /// <summary>
    /// Creates a file symlink, skipping the test when the host forbids it (Windows without
    /// Developer Mode or elevation). Skipping is honest here: the assertion is meaningless if the
    /// link was never created, and a silently-passing test would be worse than no test.
    /// </summary>
    /// <param name="target">The link target.</param>
    /// <param name="link">The link path to create.</param>
    private static void TryCreateSymlink(string target, string link)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Skip.If(true, $"This host does not permit creating symlinks: {ex.GetType().Name}");
        }
    }
}
