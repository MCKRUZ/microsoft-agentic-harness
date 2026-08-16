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
/// Security regression coverage for sandbox audit finding A6-1: <see cref="ProcessSandboxExecutor"/>
/// launched child processes that inherited the full host environment (secrets, tokens, paths).
/// These tests assert the child environment is cleared and rebuilt from an explicit,
/// closed-by-default allowlist plus per-request grants.
/// </summary>
/// <remarks>
/// Two tests here set a canary <em>process-wide</em> environment variable and clear it in a
/// <c>finally</c>, and the sandbox subprocess inherits whatever the environment holds when it spawns.
/// Membership of <see cref="ProcessEnvironmentCollection"/> keeps that hazard from ever mattering: no
/// other test in the assembly that touches the environment block can run alongside these.
/// <para>
/// It is a guard, not the fix for issue #269. That issue attributed an intermittent failure here to
/// exactly this race, and the attribution does not hold — these tests share one class, xUnit does not
/// run tests within a collection in parallel, and the assembly's only other environment-variable class
/// was already serialised. The observed failure's cause remains unmeasured; the assertions now report
/// what the sandbox actually said so the next occurrence identifies itself.
/// </para>
/// </remarks>
[Trait("Category", "WindowsOnly")]
[Collection(ProcessEnvironmentCollection.Name)]
public class ProcessSandboxEnvironmentIsolationTests
{
    private readonly Mock<IProcessResourceLimiter> _limiter = new();
    private readonly Mock<IAttestationService> _attestation = new();
    private readonly Mock<IOptionsMonitor<SandboxConfig>> _sandboxConfig = new();
    private readonly ProcessSandboxLaunchPreparer _launchPreparer;
    private readonly SandboxEgressPreflightRunner _egressPreflightRunner =
        new(null, Mock.Of<ILogger<SandboxEgressPreflightRunner>>());

    public ProcessSandboxEnvironmentIsolationTests()
    {
        _limiter.Setup(x => x.IsSupported).Returns(true);
        _limiter.Setup(x => x.Apply(It.IsAny<System.Diagnostics.Process>(), It.IsAny<ResourceLimits>()))
            .Returns(true);

        _attestation
            .Setup(x => x.SignAsync(It.IsAny<AttestationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AttestationRequest r, CancellationToken _) => r.IsFailure
                ? CreateAttestation(r.ToolName) with { IsFailureAttestation = true, OutputHash = null, FailureReason = r.FailureReason }
                : CreateAttestation(r.ToolName));

        _sandboxConfig.Setup(x => x.CurrentValue).Returns(new SandboxConfig());

        _launchPreparer = new ProcessSandboxLaunchPreparer(
            _limiter.Object, _sandboxConfig.Object, Mock.Of<ILogger<ProcessSandboxLaunchPreparer>>());
    }

    private ProcessSandboxExecutor CreateSut() => new(
        _launchPreparer,
        _attestation.Object,
        Mock.Of<ILogger<ProcessSandboxExecutor>>(),
        TimeProvider.System,
        _sandboxConfig.Object,
        _egressPreflightRunner);

    [SkippableFact]
    public async Task ExecuteAsync_HostSecretEnvVar_NotVisibleToChildProcess()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-only: uses cmd.exe environment expansion.");

        const string canaryName = "SANDBOX_CANARY_SECRET_A6";
        const string canaryValue = "leaked-host-secret-value-a6";
        Environment.SetEnvironmentVariable(canaryName, canaryValue);
        try
        {
            var request = CreateRequest(argumentList: ["/c", "echo", $"%{canaryName}%"]);

            var result = await CreateSut().ExecuteAsync(request, CancellationToken.None);

            result.Success.Should().BeTrue(WhyItFailed(result));
            result.Output.Should().NotContain(canaryValue,
                "the child process environment must be cleared — host secrets must never leak into sandboxed tools");
        }
        finally
        {
            Environment.SetEnvironmentVariable(canaryName, null);
        }
    }

    [SkippableFact]
    public async Task ExecuteAsync_AllowlistedHostVariable_FlowsToChildProcess()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-only: uses cmd.exe environment expansion.");

        // SystemRoot is in the default host allowlist and always set on Windows.
        var request = CreateRequest(argumentList: ["/c", "echo", "%SystemRoot%"]);

        var result = await CreateSut().ExecuteAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue(WhyItFailed(result));
        result.Output!.Trim().Should().Be(
            Environment.GetEnvironmentVariable("SystemRoot"),
            "allowlisted system variables must still flow through so child processes remain functional");
    }

    [SkippableFact]
    public async Task ExecuteAsync_NonAllowlistedHostVariable_BlockedEvenWhenAllowlistCustomized()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-only: uses cmd.exe environment expansion.");

        const string canaryName = "SANDBOX_CANARY_CUSTOM_A6";
        const string canaryValue = "custom-allowlist-must-not-leak";
        Environment.SetEnvironmentVariable(canaryName, canaryValue);
        try
        {
            _sandboxConfig.Setup(x => x.CurrentValue).Returns(new SandboxConfig
            {
                ProcessEnvironmentAllowlist = ["SystemRoot"]
            });

            var request = CreateRequest(argumentList: ["/c", "echo", $"%{canaryName}%"]);

            var result = await CreateSut().ExecuteAsync(request, CancellationToken.None);

            result.Success.Should().BeTrue(WhyItFailed(result));
            result.Output.Should().NotContain(canaryValue,
                "only variables named in the configured allowlist may cross the sandbox boundary");
        }
        finally
        {
            Environment.SetEnvironmentVariable(canaryName, null);
        }
    }

    [SkippableFact]
    public async Task ExecuteAsync_RequestEnvironmentGrant_VisibleToChildProcess()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-only: uses cmd.exe environment expansion.");

        var request = CreateRequest(argumentList: ["/c", "echo", "%TOOL_GRANTED_SETTING%"]) with
        {
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["TOOL_GRANTED_SETTING"] = "explicitly-granted-value"
            }
        };

        var result = await CreateSut().ExecuteAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue(WhyItFailed(result));
        result.Output.Should().Contain("explicitly-granted-value",
            "explicit per-request environment grants are the sanctioned channel for passing values into the sandbox");
    }

    [SkippableFact]
    public async Task ExecuteAsync_TempVariables_RedirectedIntoSandboxWorkspace()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-only: uses cmd.exe environment expansion.");

        var sut = CreateSut();
        string? workspaceDir = null;
        _launchPreparer.CreateWorkspaceDirectory = () =>
        {
            workspaceDir = Path.Combine(Path.GetTempPath(), $"sandbox-envtest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(workspaceDir);
            return workspaceDir;
        };

        var request = CreateRequest(argumentList: ["/c", "echo", "%TEMP%"]);

        var result = await sut.ExecuteAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue(WhyItFailed(result));
        result.Output!.Trim().Should().Be(workspaceDir,
            "TEMP must point inside the disposable sandbox workspace, not at the host temp directory");
    }

    [Theory]
    [InlineData("temp")]
    [InlineData("TMP")]
    [InlineData("tmpdir")]
    [InlineData("Path")]
    [InlineData("COMSPEC")]
    [InlineData("PathExt")]
    [InlineData("systemroot")]
    public async Task ExecuteAsync_ReservedEnvironmentGrant_RejectsRequestBeforeSpawning(string grantName)
    {
        // Reserved names are checked case-insensitively: Windows environment lookups are
        // case-insensitive, so a grant of "temp" or "Path" would otherwise override the
        // pinned temp redirection or the allowlisted PATH.
        var request = CreateRequest(argumentList: ["/c", "echo", "should-not-run"]) with
        {
            EnvironmentVariables = new Dictionary<string, string> { [grantName] = @"C:\attacker-controlled" }
        };

        var result = await CreateSut().ExecuteAsync(request, CancellationToken.None);

        result.Success.Should().BeFalse(
            "grants colliding with pinned or security-critical variables must be rejected, not silently applied or skipped");
        result.ErrorMessage.Should().Contain(grantName);
        result.Output.Should().BeNull("the child process must never be spawned for a rejected request");
        result.Attestation.Should().NotBeNull("the rejection must leave a signed audit record");
        result.Attestation!.IsFailureAttestation.Should().BeTrue();
    }

    /// <summary>
    /// Explains a failed sandbox run in the assertion message, so a failure identifies its own cause.
    /// </summary>
    /// <param name="result">The result being asserted on.</param>
    /// <returns>A reason string naming the error and exit code the run reported.</returns>
    /// <remarks>
    /// Issue #269 recorded an intermittent failure here under full-solution runs and attributed it to a
    /// race on process-wide environment variables. That hypothesis is disproven — these tests share one
    /// class, xUnit never runs tests within a collection in parallel, and the assembly's only other
    /// environment-variable class was already serialised, so no two of them could overlap. The real
    /// cause is still unmeasured, and the bare <c>BeTrue()</c> assertions were part of why: a run that
    /// timed out and one that was denied produced the same message. The next occurrence now says which.
    /// </remarks>
    private static string WhyItFailed(SandboxExecutionResult result) =>
        $"the sandboxed process was expected to run; it reported ErrorMessage='{result.ErrorMessage}', "
        + $"ExitCode={result.ExitCode?.ToString() ?? "none"} (a null exit code means it was killed or "
        + $"never started — which is what exceeding the request's 10s timeout looks like)";

    private static SandboxExecutionRequest CreateRequest(string[] argumentList) => new()
    {
        ToolName = "test_tool",
        Input = "{}",
        Limits = new ResourceLimits(),
        PermissionProfile = new ToolPermissionProfile
        {
            RequiredCapabilities = ToolCapability.None,
            AllowedPrograms = ["cmd.exe"]
        },
        Command = "cmd.exe",
        ArgumentList = argumentList,
        Timeout = TimeSpan.FromSeconds(10)
    };

    private static ToolExecutionAttestation CreateAttestation(string toolName) => new()
    {
        ToolName = toolName,
        InputHash = "test-hash",
        OutputHash = "test-output-hash",
        Timestamp = DateTimeOffset.UtcNow,
        Signature = "test-sig",
        KeyVersion = "v1",
        IsFailureAttestation = false
    };
}
