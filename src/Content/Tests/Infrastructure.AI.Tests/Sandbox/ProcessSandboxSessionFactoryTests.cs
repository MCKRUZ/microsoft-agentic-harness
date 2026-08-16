using Application.AI.Common.Interfaces.Attestation;
using Application.AI.Common.Interfaces.Sandbox;
using Domain.AI.Attestation;
using Domain.AI.Sandbox;
using Domain.Common.Config.AI.Sandbox;
using FluentAssertions;
using Infrastructure.AI.Sandbox;
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
            _attestation.Object,
            _sandboxConfig.Object,
            Mock.Of<ILogger<ProcessSandboxSession>>());
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

    [Fact]
    public async Task StartSessionAsync_ReservedEnvironmentGrant_FailsWithoutSpawning()
    {
        var request = CreateRequest() with
        {
            EnvironmentVariables = new Dictionary<string, string> { ["PATH"] = @"C:\attacker-controlled" }
        };

        var result = await _sut.StartSessionAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Contains("PATH"));
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
