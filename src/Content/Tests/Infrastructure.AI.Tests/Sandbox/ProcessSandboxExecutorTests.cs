using Application.AI.Common.Interfaces.Attestation;
using Application.AI.Common.Interfaces.Sandbox;
using Domain.AI.Attestation;
using Domain.AI.Sandbox;
using FluentAssertions;
using Infrastructure.AI.Sandbox;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Sandbox;

[Trait("Category", "WindowsOnly")]
public class ProcessSandboxExecutorTests : IDisposable
{
    private readonly Mock<IProcessResourceLimiter> _limiter = new();
    private readonly Mock<IAttestationService> _attestation = new();
    private readonly ProcessSandboxExecutor _sut;
    private readonly List<string> _createdWorkspaces = [];

    public ProcessSandboxExecutorTests()
    {
        _limiter.Setup(x => x.IsSupported).Returns(false);

        _attestation
            .Setup(x => x.SignAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string tool, string _, string __, CancellationToken ___) =>
                CreateAttestation(tool, isFailure: false));

        _attestation
            .Setup(x => x.SignFailureAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string tool, string _, string reason, CancellationToken ___) =>
                CreateAttestation(tool, isFailure: true, failureReason: reason));

        _sut = new ProcessSandboxExecutor(
            _limiter.Object,
            _attestation.Object,
            Mock.Of<ILogger<ProcessSandboxExecutor>>(),
            TimeProvider.System);
    }

    public void Dispose()
    {
        _limiter.Object.Dispose();
        foreach (var dir in _createdWorkspaces)
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_SuccessfulExecution_ReturnsOutputAndAttestation()
    {
        var request = CreateRequest(command: "cmd.exe", arguments: "/c echo hello");

        var result = await _sut.ExecuteAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().NotBeNullOrEmpty();
        result.Attestation.Should().NotBeNull();
        result.ExitCode.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_Timeout_KillsProcessAndReturnsFail()
    {
        var request = CreateRequest(
            command: "cmd.exe",
            arguments: "/c ping -n 60 127.0.0.1",
            timeout: TimeSpan.FromSeconds(2));

        var result = await _sut.ExecuteAsync(request, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("timed out");
        result.Attestation.Should().NotBeNull();
        result.Attestation!.IsFailureAttestation.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_ProcessCrash_ReturnsFailureAttestation()
    {
        var request = CreateRequest(command: "cmd.exe", arguments: "/c exit 1");

        var result = await _sut.ExecuteAsync(request, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ExitCode.Should().Be(1);
        result.Attestation.Should().NotBeNull();
        result.Attestation!.IsFailureAttestation.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_StdinInput_PassedToProcess()
    {
        var inputJson = "{\"key\":\"value\"}";
        var request = CreateRequest(
            command: "cmd.exe",
            arguments: "/c more",
            input: inputJson);

        var result = await _sut.ExecuteAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("key");
        result.Output.Should().Contain("value");
    }

    [Fact]
    public async Task ExecuteAsync_WorkspaceCleanup_DeletesTempDir()
    {
        string? capturedDir = null;
        _sut.CreateWorkspaceDirectory = () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), $"sandbox-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            capturedDir = dir;
            _createdWorkspaces.Add(dir);
            return dir;
        };

        var request = CreateRequest(command: "cmd.exe", arguments: "/c echo done");

        await _sut.ExecuteAsync(request, CancellationToken.None);

        capturedDir.Should().NotBeNull();
        Directory.Exists(capturedDir!).Should().BeFalse();
    }

    private static SandboxExecutionRequest CreateRequest(
        string command = "cmd.exe",
        string arguments = "/c echo test",
        string input = "{}",
        TimeSpan? timeout = null) => new()
    {
        ToolName = "test_tool",
        Input = input,
        Limits = new ResourceLimits(),
        PermissionProfile = new ToolPermissionProfile
        {
            RequiredCapabilities = ToolCapability.None
        },
        Command = command,
        Arguments = arguments,
        Timeout = timeout ?? TimeSpan.FromSeconds(10)
    };

    private static ToolExecutionAttestation CreateAttestation(
        string toolName, bool isFailure, string? failureReason = null) => new()
    {
        ToolName = toolName,
        InputHash = "test-hash",
        OutputHash = isFailure ? null : "test-output-hash",
        Timestamp = DateTimeOffset.UtcNow,
        Signature = "test-sig",
        KeyVersion = "v1",
        IsFailureAttestation = isFailure,
        FailureReason = failureReason
    };
}
