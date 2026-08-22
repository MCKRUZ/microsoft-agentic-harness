using Domain.Common.Config.AI.Governance;
using FluentAssertions;
using Infrastructure.AI.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Tools;

/// <summary>
/// Covers <c>DependencyInjection.ResolveGovernanceStateProtectedPaths</c> — the composition-time
/// decision about whether the governance-state directory is worth denying to the file-system tool.
/// </summary>
/// <remarks>
/// <para>
/// The decision is load-bearing well beyond the deny list. Handing
/// <see cref="FileSystemService"/> a protected path also arms its per-operation hard-link identity
/// check, and that check fails closed on any platform <c>HardLinkInspector</c> has no
/// implementation for. Arming it therefore costs macOS and BSD consumers the entire file-system
/// tool. Before this gate existed the directory was registered unconditionally, so that cost was
/// paid under every configuration — including the shipped default, where durable governance state
/// is off and no database has ever been written. That is a control armed for a feature that is not
/// running.
/// </para>
/// <para>
/// Each test asserts the gate's output <em>and</em> the boot outcome that output produces, because
/// the two halves are only interesting together. A test that checked the returned array alone would
/// still pass if <see cref="FileSystemSandboxStartupValidator"/> stopped arming on the same
/// condition, which is exactly the drift that would make a consumer's host refuse to boot over a
/// configuration with nothing to protect.
/// </para>
/// </remarks>
public sealed class GovernanceStateProtectionGateTests : IDisposable
{
    private readonly string _root;
    private readonly string _workspace;
    private readonly string _governanceStateDirectory;

    public GovernanceStateProtectionGateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"gov-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        // Siblings, mirroring the shipped layout: AllowedBasePaths ["workspace"] and the database
        // under ".agent-state". Non-overlapping on purpose — the validator reports an overlap ahead
        // of the platform limitation, so an overlapping layout would mask the branch under test.
        _workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(_workspace);
        _governanceStateDirectory = Path.Combine(_root, ".agent-state");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static GovernanceDurableStateConfig Durable(
        bool escalations = false, bool changeProposals = false, bool callOnceEnforcement = false) =>
        new()
        {
            EscalationsEnabled = escalations,
            ChangeProposalsEnabled = changeProposals,
            CallOnceEnforcementEnabled = callOnceEnforcement
        };

    /// <summary>
    /// Boots a validator over the paths the gate produced, with the hard-link platform capability
    /// forced off so the macOS branch is exercised on whatever OS the suite runs on.
    /// </summary>
    private Task BootOnAPlatformWithoutHardLinkSupport(IReadOnlyList<string> protectedPaths) =>
        new FileSystemSandboxStartupValidator(
                [_workspace],
                protectedPaths,
                NullLogger<FileSystemSandboxStartupValidator>.Instance,
                hardLinkInspectionSupported: false)
            .StartAsync(CancellationToken.None);

    [Fact]
    public async Task BothTogglesOffAndNoDirectory_RegistersNothingAndBootsOnAnUnsupportedPlatform()
    {
        // The shipped default, and the whole reason for the gate. Nothing has ever written a
        // governance-state database, so there is no file to disclose, truncate, or alias — and no
        // justification for a control whose cost is the file-system tool on macOS and the BSDs.
        Directory.Exists(_governanceStateDirectory).Should().BeFalse("the premise is that no run has created it");

        var protectedPaths = DependencyInjection.ResolveGovernanceStateProtectedPaths(
            Durable(), _governanceStateDirectory);

        protectedPaths.Should().BeEmpty(
            "with the feature off and no database on disk there is nothing to protect");

        await FluentActions.Awaiting(() => BootOnAPlatformWithoutHardLinkSupport(protectedPaths))
            .Should().NotThrowAsync(
                "a host with nothing to protect must run the file-system tool on every platform");
    }

    [Fact]
    public async Task BothTogglesOffButDirectoryPresent_RegistersItAndRefusesOnAnUnsupportedPlatform()
    {
        // The residual case, and the reason the gate cannot read the toggles alone. A database left
        // behind by a run that DID have durability enabled still holds approval verdicts; those
        // records do not stop being sensitive because someone later set a flag to false. Gating on
        // the toggles only would silently un-protect them.
        Directory.CreateDirectory(_governanceStateDirectory);

        var protectedPaths = DependencyInjection.ResolveGovernanceStateProtectedPaths(
            Durable(), _governanceStateDirectory);

        protectedPaths.Should().ContainSingle().Which.Should().Be(
            _governanceStateDirectory,
            "verdicts left by an earlier run stay sensitive after the toggles go back to false");

        await FluentActions.Awaiting(() => BootOnAPlatformWithoutHardLinkSupport(protectedPaths))
            .Should().ThrowAsync<InvalidOperationException>(
                "there is genuinely something to protect here, so a platform that cannot run the " +
                "hard-link control must be refused rather than silently left unguarded");
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public async Task EitherToggleOn_RegistersTheDirectoryEvenBeforeItExists(
        bool escalations, bool changeProposals, bool callOnceEnforcement)
    {
        // All three toggles, because any one of them on its own puts records in that database —
        // escalations write escalation_state, change proposals write change_proposal, and call-once
        // enforcement writes tool_call_ledger — and a gate that checked only a subset would leave a
        // host running just the omitted one unguarded. This is the exact shape #453/#473's own
        // Common Mistakes entry warns about: a new toggle wired everywhere except one hand-maintained
        // condition that claims to cover "either toggle."
        //
        // The directory deliberately does NOT exist yet: on a first run with durability enabled the
        // gate is evaluated at composition, before anything resolves the DbContext factory that
        // creates it. A gate that waited for the directory to appear would leave the very first run
        // — the one that populates the database — running with the deny list disarmed.
        Directory.Exists(_governanceStateDirectory).Should().BeFalse(
            "the point of this case is that the directory has not been created yet");

        var protectedPaths = DependencyInjection.ResolveGovernanceStateProtectedPaths(
            Durable(escalations, changeProposals, callOnceEnforcement), _governanceStateDirectory);

        protectedPaths.Should().ContainSingle().Which.Should().Be(
            _governanceStateDirectory,
            "the feature is on, so the database is about to exist and must be protected from the start");

        await FluentActions.Awaiting(() => BootOnAPlatformWithoutHardLinkSupport(protectedPaths))
            .Should().ThrowAsync<InvalidOperationException>(
                "durable governance state on a platform without the hard-link control is the " +
                "documented unsupported combination");
    }
}
