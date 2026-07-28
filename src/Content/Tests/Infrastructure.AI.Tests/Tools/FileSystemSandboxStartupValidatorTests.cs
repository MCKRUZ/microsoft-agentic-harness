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

    /// <summary>
    /// Builds the validator with the hard-link platform-capability signal forced, so the platform
    /// branch is exercised on whatever operating system the suite happens to run on.
    /// </summary>
    private static FileSystemSandboxStartupValidator NewSutWithHardLinkSupport(
        IReadOnlyList<string> allowedBasePaths,
        IReadOnlyList<string> protectedPaths,
        bool supported) =>
        new(allowedBasePaths,
            protectedPaths,
            NullLogger<FileSystemSandboxStartupValidator>.Instance,
            supported);

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

    [Fact]
    public async Task StartAsync_HardLinkInspectionUnsupportedWithProtectedPaths_Throws()
    {
        // The macOS case, made runnable everywhere. The hard-link control fails closed, so on a
        // platform it has no implementation for, EVERY file operation is denied — one call at a
        // time, with a message that can only say the link count was unavailable. Converting that
        // into a single boot refusal is the whole point of this check: a consumer's first hour
        // should end at a documented limitation, not at a tool that fails for no visible reason.
        var workspace = CreateDirectory("workspace");
        var agentState = CreateDirectory(".agent-state");

        var sut = NewSutWithHardLinkSupport([workspace], [agentState], supported: false);

        var ex = await sut.Invoking(s => s.StartAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>(
                "a fail-closed control with no implementation here denies every file operation, " +
                "which is worth refusing to boot over rather than discovering call by call");

        // Note the layout: workspace and .agent-state are SIBLINGS, so this is the shipped default
        // geometry the overlap check waves through. The refusal can only be coming from the
        // platform check.
        ex.Which.Message.Should().NotContain("lies inside",
            "this is the platform refusal, not the overlap refusal");

        // What an operator needs to act without reading the source.
        ex.Which.Message.Should().Contain("hard-link",
            "the message must name the control that is unavailable");
        ex.Which.Message.Should().Contain("Windows or Linux",
            "the message must name the platforms that do work");
        ex.Which.Message.Should().Contain("HardLinkInspector",
            "the message must name the type a consumer would extend to add this platform");

        // The fix an operator would otherwise reach for first, and which does not work: the
        // governance-state directory is registered as protected unconditionally in
        // RegisterToolServices, consulting no toggle, so emptying the protected list this way is
        // not reachable by configuration.
        ex.Which.Message.Should().Contain("AppConfig:AI:Governance:DurableState",
            "the message must foreclose the toggles as a fix rather than leave the operator to try it");
    }

    [Fact]
    public async Task StartAsync_HardLinkInspectionUnsupportedWithNoProtectedPaths_DoesNotThrow()
    {
        // The other half of the pair. With nothing to protect, FileSystemService skips the link
        // inspection outright (IsSingleLinked returns early on an empty protected set), so the tool
        // works normally on an unimplemented platform. The validator has to arm on exactly the same
        // condition, or it refuses to boot a host that would have run perfectly well.
        var sut = NewSutWithHardLinkSupport([_root], [], supported: false);

        await sut.Invoking(s => s.StartAsync(CancellationToken.None)).Should().NotThrowAsync(
            "with no protected paths the runtime link check never runs, so the platform is irrelevant");
    }

    [Fact]
    public async Task StartAsync_HardLinkInspectionSupportedWithProtectedPaths_DoesNotThrow()
    {
        // Pins the cause. Same protected paths as the throwing test, same sibling layout — only the
        // capability signal differs. Without this, a validator that threw whenever protected paths
        // existed at all would pass the throwing test for entirely the wrong reason.
        var workspace = CreateDirectory("workspace");
        var agentState = CreateDirectory(".agent-state");

        var sut = NewSutWithHardLinkSupport([workspace], [agentState], supported: true);

        await sut.Invoking(s => s.StartAsync(CancellationToken.None)).Should().NotThrowAsync(
            "protected paths on a platform that implements the control are the ordinary case");
    }

    [Fact]
    public async Task StartAsync_HardLinkInspectionUnsupportedWithOnlyBlankProtectedPaths_DoesNotThrow()
    {
        // FileSystemService's constructor discards blank entries before it counts, so a list of
        // blanks leaves its per-operation check disarmed. The validator mirrors that filter; a raw
        // count would refuse to boot over a configuration the tool treats as having nothing to
        // protect.
        var sut = NewSutWithHardLinkSupport([_root], ["", "   "], supported: false);

        await sut.Invoking(s => s.StartAsync(CancellationToken.None)).Should().NotThrowAsync(
            "blank entries arm nothing at runtime, so they must not arm the boot assertion either");
    }

    [Fact]
    public async Task StartAsync_OverlapOnAnUnsupportedPlatform_ReportsTheOverlapFirst()
    {
        // Both faults at once. The overlap is a security misconfiguration that survives a move to a
        // supported platform, so it is the more useful thing to say first; the operator who fixes
        // the geometry then meets the platform refusal on the next boot.
        var protectedDir = CreateDirectory(".agent-state");

        var sut = NewSutWithHardLinkSupport([_root], [protectedDir], supported: false);

        var ex = await sut.Invoking(s => s.StartAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();

        ex.Which.Message.Should().Contain("lies inside",
            "the overlap is reported ahead of the platform limitation");
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
