using FluentAssertions;
using Infrastructure.AI.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Tools;

/// <summary>
/// Covers the hard-link bypass of <see cref="FileSystemService"/>'s sandbox — an agent creating a
/// second directory entry, inside the workspace, for the harness's own governance-state database.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this geometry.</b> Every test here lays the workspace and the protected directory out as
/// SIBLINGS under one root, because that is exactly what the five hosts ship:
/// <c>AllowedBasePaths: ["workspace"]</c> and the governance database under <c>.agent-state/</c>,
/// both resolved against the application base directory. A hard link needs the same VOLUME as its
/// target, not the same subtree, so siblings on one volume are linkable and the boot-time
/// containment assertion — which passes on this layout by design — stops nothing.
/// </para>
/// <para>
/// <b>Why a path check cannot do this.</b> A hard link carries no reparse point. On the machine
/// these were written on, the link reports <c>Archive</c> attributes, a null <c>LinkTarget</c> and a
/// null <c>ResolveLinkTarget(true)</c>, so canonicalization returns the literal in-workspace path,
/// the deny list says "not protected" and the allowlist says "allowed". Only the file's link count
/// distinguishes it, which is what <see cref="HardLinkInspector"/> asks the operating system for.
/// </para>
/// <para>
/// <b>What is at stake, precisely.</b> Reading the database discloses approval payloads; writing
/// through the alias truncates the shared inode and destroys governance state. Forging a verdict is
/// NOT reachable this way — every row is HMAC-sealed and verified fail-closed on read — so these
/// tests defend against disclosure and tamper, and say so rather than overstating.
/// </para>
/// </remarks>
public sealed class FileSystemServiceHardLinkTests : IDisposable
{
    private const string DatabaseContent = "approval-verdict-payloads needle";

    private readonly string _root;
    private readonly string _workspace;
    private readonly string _protectedDir;
    private readonly string _database;
    private readonly FileSystemService _sut;

    public FileSystemServiceHardLinkTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"fss-hardlink-{Guid.NewGuid():N}");
        _workspace = Path.Combine(_root, "workspace");
        _protectedDir = Path.Combine(_root, ".agent-state");
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_protectedDir);

        _database = Path.Combine(_protectedDir, "governance-state.db");
        File.WriteAllText(_database, DatabaseContent);

        // The shipped registration: the workspace is allowed, its sibling protected directory is
        // denied. Note that the startup validator finds NO overlap here and would boot happily.
        _sut = new FileSystemService(
            NullLogger<FileSystemService>.Instance,
            [_workspace],
            [_protectedDir]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// Plants the attack: an ordinary-looking workspace file that is a second name for the database.
    /// </summary>
    /// <returns>The in-workspace path of the hard link.</returns>
    private string PlantHardLinkToDatabase()
    {
        var link = Path.Combine(_workspace, "innocent.txt");
        SandboxLinkFactory.CreateHardLink(link, _database);
        return link;
    }

    [SkippableFact]
    public async Task ReadFileAsync_HardLinkToProtectedSibling_IsDenied()
    {
        var link = PlantHardLinkToDatabase();

        var act = async () => await _sut.ReadFileAsync(link);

        await act.Should().ThrowAsync<UnauthorizedAccessException>(
            "the link names the approval-verdict database, and reading it would disclose the " +
            "approval payloads the sandbox exists to keep from the agent");
    }

    [SkippableFact]
    public async Task WriteFileAsync_HardLinkToProtectedSibling_IsDeniedAndLeavesTheDatabaseIntact()
    {
        var link = PlantHardLinkToDatabase();

        var act = async () => await _sut.WriteFileAsync(link, "truncated");

        await act.Should().ThrowAsync<UnauthorizedAccessException>(
            "writing through the alias truncates the shared inode");

        // The denial has to be the reason the file survived, so assert the file, not just the
        // throw: a guard that threw after opening the target for truncation would pass the first
        // assertion and still have destroyed the governance state.
        var onDisk = await File.ReadAllTextAsync(_database);
        onDisk.Should().Be(DatabaseContent, "the governance state must be byte-for-byte untouched");
    }

    [SkippableFact]
    public async Task SearchFilesAsync_HardLinkToProtectedSibling_DisclosesNothing()
    {
        PlantHardLinkToDatabase();

        var results = await _sut.SearchFilesAsync(_workspace, "needle");

        results.Should().BeEmpty(
            "search returns file content, so a gate on reads alone would leave the disclosure " +
            "path open: grep the workspace for a needle and read the database out of the alias");
    }

    [Fact]
    public async Task ReadFileAsync_OrdinaryWorkspaceFile_IsStillAllowed()
    {
        // The anti-vacuity guard for every test above. A link-count check that denied everything
        // would satisfy all three denials while breaking the tool outright; this is what fails if
        // the gate over-blocks, and it is deliberately a [Fact] so it can never be skipped away.
        var ordinary = Path.Combine(_workspace, "notes.txt");
        await File.WriteAllTextAsync(ordinary, "ordinary content");

        var content = await _sut.ReadFileAsync(ordinary);

        content.Should().Be("ordinary content", "a file with one directory entry is unremarkable");
    }

    [Fact]
    public async Task WriteFileAsync_NewWorkspaceFile_IsStillAllowed()
    {
        // A file that does not exist yet cannot be inspected for links. That must read as "no
        // second name", not as "cannot establish, therefore deny" — otherwise the tool can never
        // create a file at all.
        var fresh = Path.Combine(_workspace, "created.txt");

        await _sut.WriteFileAsync(fresh, "fresh content");

        (await File.ReadAllTextAsync(fresh)).Should().Be("fresh content");
    }

    [Fact]
    public async Task SearchFilesAsync_OrdinaryWorkspaceFile_IsStillFound()
    {
        // The search-path counterpart of the read anti-vacuity guard: proves the extra gate added
        // to the walk denies the alias without silently emptying every search result.
        await File.WriteAllTextAsync(Path.Combine(_workspace, "notes.txt"), "a needle here");

        var results = await _sut.SearchFilesAsync(_workspace, "needle");

        results.Should().ContainSingle("an ordinary singly-linked file is still searchable");
    }
}
