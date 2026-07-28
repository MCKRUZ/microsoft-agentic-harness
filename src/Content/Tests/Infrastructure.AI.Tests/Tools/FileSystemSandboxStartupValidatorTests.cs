using FluentAssertions;
using Infrastructure.AI.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Tools;

/// <summary>
/// Covers <see cref="FileSystemSandboxStartupValidator"/> — the boot-time assertion that no
/// protected harness-state directory lies inside a directory the file-system tool may reach.
/// </summary>
/// <remarks>
/// <para>
/// This assertion is defence in depth, and these tests deliberately do NOT claim it closes the
/// hard-link bypass. It cannot: a hard link needs the same volume as its target, not the same
/// subtree, so a layout with no containment at all is still linkable. That hole is closed by
/// <see cref="FileSystemService"/>'s per-operation link-count check and is exercised in
/// <see cref="FileSystemServiceHardLinkTests"/>. What this validator earns its place for is
/// refusing a configuration that hands the file-system tool the governance-state directory
/// outright, leaving a path-comparing deny list as the only barrier.
/// </para>
/// <para>
/// The pairing that matters here is that it fires whenever the geometry is wrong and stays silent
/// on the shipped default, because a guard that fires on the default configuration gets turned off
/// rather than heeded.
/// </para>
/// </remarks>
public sealed class FileSystemSandboxStartupValidatorTests : IDisposable
{
    private readonly string _root;

    public FileSystemSandboxStartupValidatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"fss-guard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static FileSystemSandboxStartupValidator NewSut(
        IReadOnlyList<string> allowedBasePaths,
        IReadOnlyList<string> protectedPaths) =>
        new(allowedBasePaths,
            protectedPaths,
            NullLogger<FileSystemSandboxStartupValidator>.Instance);

    private string CreateDirectory(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public async Task StartAsync_ProtectedDirectoryInsideAllowedBasePath_Throws()
    {
        var protectedDir = CreateDirectory(".agent-state");

        var sut = NewSut([_root], [protectedDir]);

        var ex = await sut.Invoking(s => s.StartAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>(
                "granting the file-system tool the governance-state directory leaves a " +
                "path-comparing deny list as the only barrier to the approval-verdict database");

        // The operator has to be able to act on this without reading the source.
        ex.Which.Message.Should().Contain(protectedDir, "the message must name the offending protected path");
        ex.Which.Message.Should().Contain(_root, "the message must name the allowed base path containing it");
        ex.Which.Message.Should().Contain("AppConfig:Infrastructure:FileSystem:AllowedBasePaths",
            "the message must name the setting to change");
    }

    [Fact]
    public async Task StartAsync_ProtectedDirectoryEqualsAllowedBasePath_Throws()
    {
        // Equality is containment's boundary case: granting the tool the protected directory
        // itself is the most direct form of the same misconfiguration.
        var sut = NewSut([_root], [_root]);

        await sut.Invoking(s => s.StartAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task StartAsync_ShippedDefaultLayout_DoesNotThrow()
    {
        // Mirrors what the five hosts actually register: AllowedBasePaths ["workspace"] resolved
        // against the application base directory, and the governance database under
        // ".agent-state/" — a SIBLING of workspace, not a child. If this ever starts throwing, the
        // safe default has drifted and every host stops booting.
        //
        // Read this alongside FileSystemServiceHardLinkTests: the very layout green-lit here is
        // same-volume, so it IS hard-linkable. This test asserts the validator tolerates the
        // shipped default, not that the shipped default is safe on its own.
        var workspace = CreateDirectory("workspace");
        var agentState = CreateDirectory(".agent-state");

        var sut = NewSut([workspace], [agentState]);

        await sut.Invoking(s => s.StartAsync(CancellationToken.None)).Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_SiblingWhoseNameSharesThePrefix_DoesNotThrow()
    {
        // Guards the directory-boundary rule in the assertion itself: "workspace-archive" is a
        // legitimate sibling of "workspace". A plain string-prefix check would fail the boot.
        var workspace = CreateDirectory("workspace");
        var lookalike = CreateDirectory("workspace-archive");

        var sut = NewSut([workspace], [lookalike]);

        await sut.Invoking(s => s.StartAsync(CancellationToken.None)).Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_OverlapWithDurableStateDisabled_StillThrows()
    {
        // The validator takes no configuration and makes no exception for the durable-state
        // toggles being off. It used to: the reasoning was that the toggles are restart-required,
        // so enabling one would re-run this validator before any database could be written. That
        // reasoning is false. EscalationReconciliationService.RunPruneAsync re-reads
        // AI:Governance:DurableState from IOptionsMonitor on every reconcile tick and AppConfig is
        // loaded with reloadOnChange: true, so an operator who edits ChangeProposalsEnabled to true
        // on a RUNNING host creates the database on a host this validator already waved through.
        // A boot assertion a live config edit can invalidate has to hold on every boot.
        var protectedDir = CreateDirectory(".agent-state");

        var sut = NewSut([_root], [protectedDir]);

        await sut.Invoking(s => s.StartAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>(
                "the overlap is refused regardless of whether durable state is currently enabled");
    }

    [Fact]
    public async Task StartAsync_NoProtectedPathsConfigured_DoesNotThrow()
    {
        var sut = NewSut([_root], []);

        await sut.Invoking(s => s.StartAsync(CancellationToken.None)).Should().NotThrowAsync();
    }

    [SkippableFact]
    public async Task StartAsync_OverlapReachableOnlyThroughALinkedBasePath_Throws()
    {
        // The overlap is invisible to a literal string comparison. The allowed base path is
        // configured as "base-link", which shares no prefix with the protected directory as
        // written; only after resolving the link do the two land in the same tree. Canonicalizing
        // BOTH sides is what catches it — canonicalizing only the protected path would not.
        var appDirectory = CreateDirectory("app");
        var protectedDir = CreateDirectory("app", ".agent-state");

        var linkedBase = Path.Combine(_root, "base-link");
        SandboxLinkFactory.CreateDirectoryLink(linkedBase, appDirectory);

        var sut = NewSut([linkedBase], [protectedDir]);

        await sut.Invoking(s => s.StartAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>(
                "the allowed base path resolves onto the tree holding the protected directory");
    }
}
