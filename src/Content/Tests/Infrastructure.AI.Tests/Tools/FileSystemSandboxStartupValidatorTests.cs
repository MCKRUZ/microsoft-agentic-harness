using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Tests.Changes.Support;
using Infrastructure.AI.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Infrastructure.AI.Tests.Tools;

/// <summary>
/// Covers <see cref="FileSystemSandboxStartupValidator"/> — the boot-time assertion that no
/// protected harness-state directory lies inside a directory the file-system tool may reach.
/// </summary>
/// <remarks>
/// This assertion, not the deny list, is what closes the hard-link bypass. A hard link is a second
/// directory entry for the same file rather than a reparse point, so it resolves to itself and
/// presents to every per-path check as an ordinary unprotected file; read through one, the
/// approval-verdict database is readable, and written through one it is forgeable. A hard link
/// cannot span volumes, so keeping the protected directory outside every allowed base path makes
/// the bypass unconstructible. These tests pin that the assertion fires when it must and stays
/// silent on the shipped default, because a guard that fires on the default configuration would be
/// turned off rather than heeded.
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

    private static AppConfig ConfigWithDurableState(bool escalations, bool proposals)
    {
        var config = new AppConfig();
        config.AI.Governance.DurableState.EscalationsEnabled = escalations;
        config.AI.Governance.DurableState.ChangeProposalsEnabled = proposals;
        return config;
    }

    private static FileSystemSandboxStartupValidator NewSut(
        IReadOnlyList<string> allowedBasePaths,
        IReadOnlyList<string> protectedPaths,
        AppConfig config) =>
        new(allowedBasePaths,
            protectedPaths,
            new TestConfig.StaticOptionsMonitor<AppConfig>(config),
            NullLogger<FileSystemSandboxStartupValidator>.Instance);

    private string CreateDirectory(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public async Task StartAsync_ProtectedDirectoryInsideAllowedBasePath_DurableStateEnabled_Throws()
    {
        var protectedDir = CreateDirectory(".agent-state");

        var sut = NewSut([_root], [protectedDir], ConfigWithDurableState(escalations: true, proposals: false));

        var ex = await sut.Invoking(s => s.StartAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>(
                "a hard link inside the allowed tree would alias the approval-verdict database, " +
                "and no per-path check can see through a hard link");

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
        var sut = NewSut([_root], [_root], ConfigWithDurableState(escalations: false, proposals: true));

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
        var workspace = CreateDirectory("workspace");
        var agentState = CreateDirectory(".agent-state");

        var sut = NewSut([workspace], [agentState], ConfigWithDurableState(escalations: true, proposals: true));

        await sut.Invoking(s => s.StartAsync(CancellationToken.None)).Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_SiblingWhoseNameSharesThePrefix_DoesNotThrow()
    {
        // Guards the directory-boundary rule in the assertion itself: "workspace-archive" is a
        // legitimate sibling of "workspace". A plain string-prefix check would fail the boot.
        var workspace = CreateDirectory("workspace");
        var lookalike = CreateDirectory("workspace-archive");

        var sut = NewSut([workspace], [lookalike], ConfigWithDurableState(escalations: true, proposals: false));

        await sut.Invoking(s => s.StartAsync(CancellationToken.None)).Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_OverlapButDurableStateDisabled_DoesNotThrow()
    {
        // Both toggles off means no database is ever created, so the protected directory holds
        // nothing and refusing to boot would cost a consumer their widened Development allowlist
        // for no security gain. The toggles are restart-required, so enabling one re-runs this.
        var protectedDir = CreateDirectory(".agent-state");

        var sut = NewSut([_root], [protectedDir], ConfigWithDurableState(escalations: false, proposals: false));

        await sut.Invoking(s => s.StartAsync(CancellationToken.None)).Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_NoProtectedPathsConfigured_DoesNotThrow()
    {
        var sut = NewSut([_root], [], ConfigWithDurableState(escalations: true, proposals: true));

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

        var sut = NewSut([linkedBase], [protectedDir], ConfigWithDurableState(escalations: true, proposals: false));

        await sut.Invoking(s => s.StartAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>(
                "the allowed base path resolves onto the tree holding the protected directory");
    }
}
