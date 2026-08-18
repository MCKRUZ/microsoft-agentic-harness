using Application.AI.Common.Interfaces.Attestation;
using Application.AI.Common.Interfaces.Sandbox;
using Domain.AI.Attestation;
using Domain.AI.Sandbox;
using Domain.Common.Config.AI.Sandbox;
using FluentAssertions;
using Infrastructure.AI.Sandbox;
using Infrastructure.AI.Tests.Sandbox.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Sandbox;

/// <summary>
/// Coverage for <see cref="ProcessSandboxSessionFactory"/> — #371's long-lived, duplex
/// counterpart to <see cref="ProcessSandboxExecutor"/>. The interactive test is the one that
/// matters most here: every pre-existing stdio test in this assembly only asserts failure
/// against a nonexistent binary, so this is the first test that actually proves a two-way,
/// multi-message conversation with a sandboxed process works, not just that it can be rejected.
/// </summary>
[Trait("Category", "WindowsOnly")]
public class ProcessSandboxSessionFactoryTests
{
    private readonly Mock<IProcessResourceLimiter> _limiter = new();
    private readonly Mock<IOptionsMonitor<SandboxConfig>> _sandboxConfig = new();
    private readonly Mock<IAttestationService> _attestation = new();
    private readonly ProcessSandboxLaunchPreparer _launchPreparer;
    private readonly ProcessSandboxSessionFactory _sut;

    public ProcessSandboxSessionFactoryTests()
    {
        _limiter.Setup(x => x.IsSupported).Returns(true);
        _limiter.Setup(x => x.Apply(It.IsAny<System.Diagnostics.Process>(), It.IsAny<ResourceLimits>()))
            .Returns(true);

        _sandboxConfig.Setup(x => x.CurrentValue).Returns(new SandboxConfig { Enabled = true });

        _attestation
            .Setup(x => x.SignAsync(It.IsAny<AttestationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AttestationRequest r, CancellationToken _) => new ToolExecutionAttestation
            {
                ToolName = r.ToolName,
                InputHash = "test-hash",
                OutputHash = null,
                Timestamp = DateTimeOffset.UtcNow,
                Signature = "test-sig",
                KeyVersion = "v1",
                IsFailureAttestation = r.IsFailure,
                FailureReason = r.FailureReason
            });

        _launchPreparer = new ProcessSandboxLaunchPreparer(
            _limiter.Object, _sandboxConfig.Object, Mock.Of<ILogger<ProcessSandboxLaunchPreparer>>());

        _sut = new ProcessSandboxSessionFactory(
            _launchPreparer,
            new SandboxEgressPreflightRunner(null, Mock.Of<ILogger<SandboxEgressPreflightRunner>>()),
            new SandboxSessionAttestationSigner(_attestation.Object),
            _sandboxConfig.Object,
            Mock.Of<ILogger<ProcessSandboxSession>>());
    }

    [SkippableFact]
    public async Task StartSessionAsync_Success_SignsStartAttestation()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-only: uses cmd.exe /c more.");

        var result = await _sut.StartSessionAsync(CreateRequest(), CancellationToken.None);
        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        await using var session = result.Value!;

        _attestation.Verify(x => x.SignAsync(
            It.Is<AttestationRequest>(r => !r.IsFailure && r.ToolName == "test_session_tool"),
            It.IsAny<CancellationToken>()), Times.Once,
            "a session that actually ran untrusted bundle code must leave a signed audit record — " +
            "the more consequential event than any of the rejection paths, which were already attested");
    }

    [SkippableFact]
    public async Task StartSessionAsync_ProcessTier_AttestsCapabilitiesAsDeclarationOnly()
    {
        // A security-review finding on #405's follow-up: neither ProcessSandboxExecutor nor
        // ProcessSandboxLaunchPreparer read Isolation or any capability bit, so a Process-tier
        // attestation's signed capability set is not an enforced boundary — only Container-tier is.
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-only: uses cmd.exe /c more.");

        var getSigned = _attestation.CaptureNonFailureAttestation();

        var result = await _sut.StartSessionAsync(CreateRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        await using var session = result.Value!;
        var signed = getSigned();
        signed.Should().NotBeNull();
        System.Text.Json.JsonDocument.Parse(signed!.Input).RootElement
            .GetProperty("capabilitiesEnforcedBy").GetString().Should().Be("declaration-only");
    }

    [SkippableFact]
    public async Task StartSessionAsync_InteractiveProcess_SupportsMultipleWriteReadRoundTrips()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-only: uses cmd.exe /c more as a line-streaming echo.");

        var result = await _sut.StartSessionAsync(CreateRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        await using var session = result.Value!;

        using var reader = new StreamReader(session.StandardOutput, leaveOpen: true);
        using var writer = new StreamWriter(session.StandardInput, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };

        // Two independent round trips over the SAME session/process — the property a one-shot
        // ISandboxExecutor cannot offer (its stdin is written once and closed immediately).
        await writer.WriteLineAsync("first-message");
        var firstLine = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        firstLine.Should().Be("first-message");

        await writer.WriteLineAsync("second-message");
        var secondLine = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        secondLine.Should().Be("second-message");

        session.Completion.IsCompleted.Should().BeFalse(
            "the process must still be running between messages, not exited after the first exchange");
    }

    [SkippableFact]
    public async Task DisposeAsync_RunningSession_TerminatesProcessAndCleansUpWorkspace()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-only: uses cmd.exe /c more.");

        string? capturedWorkspace = null;
        _launchPreparer.CreateWorkspaceDirectory = () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"sandbox-session-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            capturedWorkspace = dir;
            return dir;
        };

        var result = await _sut.StartSessionAsync(CreateRequest(), CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        var session = result.Value!;

        await session.DisposeAsync();

        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        capturedWorkspace.Should().NotBeNull();
        Directory.Exists(capturedWorkspace!).Should().BeFalse("disposal must clean up the session's workspace");
    }

    [Fact]
    public async Task StartSessionAsync_SandboxDisabled_FailsWithoutSpawning()
    {
        _sandboxConfig.Setup(x => x.CurrentValue).Returns(new SandboxConfig { Enabled = false });

        var result = await _sut.StartSessionAsync(CreateRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Contains("Sandbox:Enabled=false"));
    }

    [Fact]
    public async Task StartSessionAsync_WorkspaceSeedRequested_RejectedBeforeSpawning()
    {
        // #371: this tier is not a containment boundary, so a request carrying caller-supplied content
        // to seed the workspace (e.g. a bundle's staged files) must be refused outright — never silently
        // downgraded from the Container tier that seeding actually requires.
        var request = CreateRequest() with { WorkspaceSeedDirectory = Path.GetTempPath() };

        var result = await _sut.StartSessionAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Contains("container isolation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StartSessionAsync_CommandNotAllowlisted_FailsWithoutSpawning()
    {
        var request = CreateRequest() with
        {
            PermissionProfile = new ToolPermissionProfile
            {
                RequiredCapabilities = ToolCapability.None,
                AllowedPrograms = ["notepad.exe"]
            }
        };

        var result = await _sut.StartSessionAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Contains("not in the allowed programs list"));
    }

    [Theory]
    [InlineData("PATH")]
    [InlineData("LD_PRELOAD")]
    public async Task StartSessionAsync_ReservedEnvironmentGrant_FailsWithoutSpawning(string grantName)
    {
        var request = CreateRequest() with
        {
            EnvironmentVariables = new Dictionary<string, string> { [grantName] = @"C:\attacker-controlled" }
        };

        var result = await _sut.StartSessionAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Contains(grantName));
    }

    [Fact]
    public async Task StartSessionAsync_CommandNotAllowlisted_SignsFailureAttestation()
    {
        var request = CreateRequest() with
        {
            PermissionProfile = new ToolPermissionProfile
            {
                RequiredCapabilities = ToolCapability.None,
                AllowedPrograms = ["notepad.exe"]
            }
        };

        await _sut.StartSessionAsync(request, CancellationToken.None);

        _attestation.Verify(x => x.SignAsync(
            It.Is<AttestationRequest>(r => r.IsFailure && r.ToolName == request.ToolName),
            It.IsAny<CancellationToken>()), Times.Once,
            "a security-relevant rejection (command not allowlisted) must leave a signed audit record, " +
            "matching what the one-shot ProcessSandboxExecutor does for the identical rejection");
    }

    [SkippableFact]
    public async Task StartSessionAsync_AllowlistedButUnstartableCommand_ReturnsFailureInsteadOfThrowing()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-only: Process.Start()'s failure shape for a missing binary is platform-specific.");

        // Passes the allowlist check (it's a string match, not an existence check) but Process.Start()
        // throws Win32Exception — NOT UnauthorizedAccessException — because the binary doesn't exist.
        // Before the fix this propagated uncaught, breaking ISandboxSessionFactory's documented
        // "never throws" contract and leaking the (never-started) workspace directory.
        const string missingBinary = "definitely-nonexistent-sandbox-binary-xyz.exe";
        var request = CreateRequest() with
        {
            PermissionProfile = new ToolPermissionProfile
            {
                RequiredCapabilities = ToolCapability.None,
                AllowedPrograms = [missingBinary]
            },
            Command = missingBinary,
            ArgumentList = []
        };

        var result = await _sut.StartSessionAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        _attestation.Verify(x => x.SignAsync(
            It.Is<AttestationRequest>(r => r.IsFailure), It.IsAny<CancellationToken>()), Times.Once);
    }

    [SkippableFact]
    public async Task StartSessionAsync_CallerCancelsAfterProcessStarted_SessionStillCompletesAndIsAttested()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-only: spawns a real cmd.exe process.");

        // By the time ApplyResourceLimits returns, the real process is already running — the
        // untrusted command is already executing. SignStartAsync deliberately no longer takes the
        // caller's token (see its own remarks): abandoning the "session started" attestation for
        // an event that has unconditionally already happened would leave the audit trail with
        // neither a start nor a failure record. So cancelling ct at this exact point must NOT
        // abort the session — it must complete normally and still get its start attestation.
        // (A caller that genuinely wants out after this point disposes the returned session or
        // lets the outer MCP handshake's own cancellation handle it — a separate, already-covered
        // path — rather than this factory silently discarding a session that is already running.)
        using var cts = new CancellationTokenSource();
        _limiter.Setup(x => x.Apply(It.IsAny<System.Diagnostics.Process>(), It.IsAny<ResourceLimits>()))
            .Returns<System.Diagnostics.Process, ResourceLimits>((_, _) =>
            {
                cts.Cancel();
                return true;
            });

        var result = await _sut.StartSessionAsync(CreateRequest(), cts.Token);

        result.IsSuccess.Should().BeTrue(
            "the session already exists once the process is spawned — the caller's token firing " +
            "after that point must not discard a session that is already running");
        await using var session = result.Value!;
        _attestation.Verify(x => x.SignAsync(
                It.Is<AttestationRequest>(r => !r.IsFailure), It.IsAny<CancellationToken>()),
            Times.Once,
            "the audit trail must still record that a session actually started, even though the " +
            "caller's token was already cancelled by the time the attestation call ran");
    }

    [SkippableFact]
    public async Task StartSessionAsync_InvalidMaxSessionDuration_KillsProcessAndReturnsFailureInsteadOfThrowing()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-only: spawns a real cmd.exe process.");

        // A negative (but not -1ms/"infinite") duration makes the session's internal
        // CancellationTokenSource throw ArgumentOutOfRangeException — after the process has
        // already been started and had resource limits applied. Before the fix, nothing caught
        // this: the already-running process, its Job Object handle, and its workspace all leaked.
        var request = CreateRequest() with { MaxSessionDuration = TimeSpan.FromMilliseconds(-500) };

        var result = await _sut.StartSessionAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        _attestation.Verify(x => x.SignAsync(
            It.Is<AttestationRequest>(r => r.IsFailure), It.IsAny<CancellationToken>()), Times.Once);
        _attestation.Verify(x => x.SignAsync(
                It.Is<AttestationRequest>(r => !r.IsFailure), It.IsAny<CancellationToken>()),
            Times.Never,
            "the session constructor throws before a session is ever returned to the caller — signing a " +
            "success attestation here would record 'session started' for a session nobody ever received");
    }

    private static SandboxSessionRequest CreateRequest() => new()
    {
        ToolName = "test_session_tool",
        Limits = new ResourceLimits(),
        PermissionProfile = new ToolPermissionProfile
        {
            RequiredCapabilities = ToolCapability.None,
            AllowedPrograms = ["cmd.exe"]
        },
        Command = "cmd.exe",
        ArgumentList = ["/c", "more"],
        MaxSessionDuration = TimeSpan.FromSeconds(30)
    };
}
