using Application.AI.Common.Interfaces.Attestation;
using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Models.Sandbox;
using Docker.DotNet;
using Docker.DotNet.Models;
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
/// Security regression coverage for sandbox audit finding A6-2 at the executor level:
/// crash results carried an <c>Output</c> that the failure attestation never covered, so
/// the returned output could diverge from the signed record without detection. These tests
/// assert both executors bind the actually produced output into the failure attestation.
/// </summary>
public class SandboxAttestationBindingTests
{
    private static ToolExecutionAttestation CreateAttestation(
        string toolName, bool isFailure, string? outputHash = null) => new()
    {
        ToolName = toolName,
        InputHash = "test-hash",
        OutputHash = outputHash,
        Timestamp = DateTimeOffset.UtcNow,
        Signature = "test-sig",
        KeyVersion = "v1",
        IsFailureAttestation = isFailure
    };

    [Trait("Category", "WindowsOnly")]
    public class ProcessCrashBinding
    {
        private readonly Mock<IProcessResourceLimiter> _limiter = new();
        private readonly Mock<IAttestationService> _attestation = new();
        private readonly ProcessSandboxExecutor _sut;

        public ProcessCrashBinding()
        {
            _limiter.Setup(x => x.IsSupported).Returns(true);
            _limiter.Setup(x => x.Apply(It.IsAny<System.Diagnostics.Process>(), It.IsAny<ResourceLimits>()))
                .Returns(true);

            _attestation
                .Setup(x => x.SignAsync(It.IsAny<AttestationRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((AttestationRequest r, CancellationToken _) =>
                    CreateAttestation(r.ToolName, isFailure: r.IsFailure,
                        outputHash: r.Output is not null ? "bound-hash" : null));

            var sandboxConfig = new Mock<IOptionsMonitor<SandboxConfig>>();
            sandboxConfig.Setup(x => x.CurrentValue).Returns(new SandboxConfig());

            _sut = new ProcessSandboxExecutor(
                _limiter.Object,
                _attestation.Object,
                Mock.Of<ILogger<ProcessSandboxExecutor>>(),
                TimeProvider.System,
                sandboxConfig.Object);
        }

        [SkippableFact]
        public async Task ExecuteAsync_ProcessCrashWithStdout_BindsStdoutIntoFailureAttestation()
        {
            Skip.IfNot(OperatingSystem.IsWindows(), "Windows-only: uses cmd.exe.");

            var request = new SandboxExecutionRequest
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
                ArgumentList = ["/c", "echo", "crash-stdout-data", "&", "exit", "3"],
                Timeout = TimeSpan.FromSeconds(10)
            };

            var result = await _sut.ExecuteAsync(request, CancellationToken.None);

            result.Success.Should().BeFalse();
            result.ExitCode.Should().Be(3);
            result.Output.Should().Contain("crash-stdout-data");

            _attestation.Verify(x => x.SignAsync(
                    It.Is<AttestationRequest>(r =>
                        r.ToolName == "test_tool"
                        && r.IsFailure
                        && r.Output == result.Output
                        && r.EgressDigest == null),
                    It.IsAny<CancellationToken>()),
                Times.Once,
                "the crash output returned to the caller must be the exact bytes bound into the attestation");
        }
    }

    public class DockerCrashBinding
    {
        private readonly Mock<IDockerClient> _dockerClient = new();
        private readonly Mock<IContainerOperations> _containers = new();
        private readonly Mock<IImageOperations> _images = new();
        private readonly Mock<ISystemOperations> _system = new();
        private readonly Mock<IAttestationService> _attestation = new();
        private readonly Mock<IOptionsMonitor<SandboxExecutionOptions>> _options = new();
        private readonly DockerSandboxExecutor _sut;
        private string? _capturedWorkspaceDir;

        public DockerCrashBinding()
        {
            _dockerClient.Setup(x => x.Containers).Returns(_containers.Object);
            _dockerClient.Setup(x => x.System).Returns(_system.Object);
            _dockerClient.Setup(x => x.Images).Returns(_images.Object);

            _system.Setup(x => x.PingAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            _images.Setup(x => x.InspectImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ImageInspectResponse());

            _containers.Setup(x => x.CreateContainerAsync(
                    It.IsAny<CreateContainerParameters>(), It.IsAny<CancellationToken>()))
                .Callback<CreateContainerParameters, CancellationToken>((p, _) =>
                {
                    var bind = p.HostConfig.Binds[0];
                    _capturedWorkspaceDir = bind[..bind.IndexOf(":/workspace", StringComparison.Ordinal)];
                })
                .ReturnsAsync(new CreateContainerResponse { ID = "test-container-id" });

            _containers.Setup(x => x.StartContainerAsync(
                    It.IsAny<string>(), It.IsAny<ContainerStartParameters>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            _containers.Setup(x => x.GetContainerLogsAsync(
                    It.IsAny<string>(), It.IsAny<bool>(),
                    It.IsAny<ContainerLogsParameters>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MultiplexedStream(Stream.Null, default));

            _containers.Setup(x => x.RemoveContainerAsync(
                    It.IsAny<string>(), It.IsAny<ContainerRemoveParameters>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            _attestation
                .Setup(x => x.SignAsync(It.IsAny<AttestationRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((AttestationRequest r, CancellationToken _) =>
                    CreateAttestation(r.ToolName, isFailure: r.IsFailure,
                        outputHash: r.Output is not null ? "bound-hash" : null));

            _options.Setup(x => x.CurrentValue).Returns(new SandboxExecutionOptions());

            var sandboxConfig = new Mock<IOptionsMonitor<SandboxConfig>>();
            sandboxConfig.Setup(x => x.CurrentValue).Returns(new SandboxConfig { Enabled = true });

            _sut = new DockerSandboxExecutor(
                _dockerClient.Object,
                _attestation.Object,
                _options.Object,
                sandboxConfig.Object,
                Mock.Of<ILogger<DockerSandboxExecutor>>());
        }

        [Fact]
        public async Task ExecuteAsync_ContainerCrashWithWorkspaceOutput_BindsOutputIntoFailureAttestation()
        {
            const string producedOutput = "{\"partial\":\"result-before-crash\"}";

            _containers.Setup(x => x.WaitContainerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, CancellationToken>(async (_, _) =>
                {
                    await File.WriteAllTextAsync(
                        Path.Combine(_capturedWorkspaceDir!, "output.json"), producedOutput);
                    return new ContainerWaitResponse { StatusCode = 1 };
                });

            var result = await _sut.ExecuteAsync(CreateRequest(), CancellationToken.None);

            result.Success.Should().BeFalse();
            result.Output.Should().Be(producedOutput);

            _attestation.Verify(x => x.SignAsync(
                    It.Is<AttestationRequest>(r =>
                        r.ToolName == "test_tool"
                        && r.IsFailure
                        && r.Output == producedOutput
                        && r.EgressDigest == null),
                    It.IsAny<CancellationToken>()),
                Times.Once,
                "the workspace output returned on a container crash must be bound into the attestation");
        }

        [Fact]
        public async Task ExecuteAsync_ContainerCrashWithoutOutput_UsesLegacyFailureAttestation()
        {
            _containers.Setup(x => x.WaitContainerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ContainerWaitResponse { StatusCode = 1 });

            var result = await _sut.ExecuteAsync(CreateRequest(), CancellationToken.None);

            result.Success.Should().BeFalse();
            result.Output.Should().BeNull();

            _attestation.Verify(x => x.SignAsync(
                    It.Is<AttestationRequest>(r =>
                        r.ToolName == "test_tool" && r.IsFailure && r.Output == null),
                    It.IsAny<CancellationToken>()),
                Times.Once);
            _attestation.Verify(x => x.SignAsync(
                    It.Is<AttestationRequest>(r => r.IsFailure && r.Output != null),
                    It.IsAny<CancellationToken>()),
                Times.Never,
                "when no output was produced there is nothing to bind — the legacy failure shape applies");
        }

        private static SandboxExecutionRequest CreateRequest() => new()
        {
            ToolName = "test_tool",
            Input = "{\"action\":\"test\"}",
            Limits = new ResourceLimits(),
            PermissionProfile = new ToolPermissionProfile
            {
                RequiredCapabilities = ToolCapability.None,
                MinimumIsolation = SandboxIsolationLevel.Process
            },
            Timeout = TimeSpan.FromSeconds(30)
        };
    }
}
