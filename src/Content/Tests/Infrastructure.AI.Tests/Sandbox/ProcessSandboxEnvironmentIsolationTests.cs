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
[Trait("Category", "WindowsOnly")]
public class ProcessSandboxEnvironmentIsolationTests
{
    private readonly Mock<IProcessResourceLimiter> _limiter = new();
    private readonly Mock<IAttestationService> _attestation = new();
    private readonly Mock<IOptionsMonitor<SandboxConfig>> _sandboxConfig = new();

    public ProcessSandboxEnvironmentIsolationTests()
    {
        _limiter.Setup(x => x.IsSupported).Returns(true);
        _limiter.Setup(x => x.Apply(It.IsAny<System.Diagnostics.Process>(), It.IsAny<ResourceLimits>()))
            .Returns(true);

        _attestation
            .Setup(x => x.SignAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string tool, string _, string __, CancellationToken ___) =>
                CreateAttestation(tool));

        _sandboxConfig.Setup(x => x.CurrentValue).Returns(new SandboxConfig());
    }

    private ProcessSandboxExecutor CreateSut() => new(
        _limiter.Object,
        _attestation.Object,
        Mock.Of<ILogger<ProcessSandboxExecutor>>(),
        TimeProvider.System,
        _sandboxConfig.Object);

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

            result.Success.Should().BeTrue();
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

        result.Success.Should().BeTrue();
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

            result.Success.Should().BeTrue();
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

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("explicitly-granted-value",
            "explicit per-request environment grants are the sanctioned channel for passing values into the sandbox");
    }

    [SkippableFact]
    public async Task ExecuteAsync_TempVariables_RedirectedIntoSandboxWorkspace()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows-only: uses cmd.exe environment expansion.");

        var sut = CreateSut();
        string? workspaceDir = null;
        sut.CreateWorkspaceDirectory = () =>
        {
            workspaceDir = Path.Combine(Path.GetTempPath(), $"sandbox-envtest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(workspaceDir);
            return workspaceDir;
        };

        var request = CreateRequest(argumentList: ["/c", "echo", "%TEMP%"]);

        var result = await sut.ExecuteAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output!.Trim().Should().Be(workspaceDir,
            "TEMP must point inside the disposable sandbox workspace, not at the host temp directory");
    }

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
