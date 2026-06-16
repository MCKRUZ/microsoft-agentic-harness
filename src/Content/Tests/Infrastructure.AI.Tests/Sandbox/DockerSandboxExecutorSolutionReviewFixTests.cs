using Application.AI.Common.Interfaces.Attestation;
using Application.AI.Common.Models.Sandbox;
using Docker.DotNet;
using Docker.DotNet.Models;
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
/// Regression coverage for solution-review finding #32: <see cref="DockerSandboxExecutor"/>
/// ignored the documented global sandbox kill-switch (<c>Sandbox:Enabled=false</c>), so
/// container-isolated tools continued to execute while process-isolated tools were refused.
/// These tests assert the Docker path now honors the same invariant as
/// <c>ProcessSandboxExecutor</c>.
/// </summary>
public class DockerSandboxExecutorSolutionReviewFixTests
{
    private readonly Mock<IDockerClient> _dockerClient = new();
    private readonly Mock<IContainerOperations> _containers = new();
    private readonly Mock<IImageOperations> _images = new();
    private readonly Mock<ISystemOperations> _system = new();
    private readonly Mock<IAttestationService> _attestation = new();
    private readonly Mock<IOptionsMonitor<SandboxExecutionOptions>> _options = new();
    private readonly Mock<IOptionsMonitor<SandboxConfig>> _sandboxConfig = new();

    public DockerSandboxExecutorSolutionReviewFixTests()
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
            .ReturnsAsync(new CreateContainerResponse { ID = "test-container-id" });

        _containers.Setup(x => x.StartContainerAsync(
                It.IsAny<string>(), It.IsAny<ContainerStartParameters>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _containers.Setup(x => x.WaitContainerAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerWaitResponse { StatusCode = 0 });

        _containers.Setup(x => x.GetContainerLogsAsync(
                It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<ContainerLogsParameters>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MultiplexedStream(Stream.Null, default));

        _containers.Setup(x => x.RemoveContainerAsync(
                It.IsAny<string>(), It.IsAny<ContainerRemoveParameters>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _options.Setup(x => x.CurrentValue).Returns(new SandboxExecutionOptions());
    }

    private DockerSandboxExecutor CreateSut() => new(
        _dockerClient.Object,
        _attestation.Object,
        _options.Object,
        _sandboxConfig.Object,
        Mock.Of<ILogger<DockerSandboxExecutor>>());

    [Fact]
    public async Task ExecuteAsync_SandboxDisabled_ThrowsAndNeverCreatesContainer()
    {
        _sandboxConfig.Setup(x => x.CurrentValue).Returns(new SandboxConfig { Enabled = false });
        var sut = CreateSut();
        var request = CreateRequest(minIsolation: SandboxIsolationLevel.Container);

        var act = () => sut.ExecuteAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Sandbox:Enabled=false*");
        _containers.Verify(x => x.CreateContainerAsync(
            It.IsAny<CreateContainerParameters>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_SandboxEnabled_ExecutesContainer()
    {
        _sandboxConfig.Setup(x => x.CurrentValue).Returns(new SandboxConfig { Enabled = true });
        _attestation
            .Setup(x => x.SignAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateAttestation("test_tool", isFailure: false));
        var sut = CreateSut();
        var request = CreateRequest();

        var result = await sut.ExecuteAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        _containers.Verify(x => x.CreateContainerAsync(
            It.IsAny<CreateContainerParameters>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static SandboxExecutionRequest CreateRequest(
        SandboxIsolationLevel minIsolation = SandboxIsolationLevel.Process) => new()
    {
        ToolName = "test_tool",
        Input = "{\"action\":\"test\"}",
        Limits = new ResourceLimits { MemoryLimitBytes = 256 * 1024 * 1024 },
        PermissionProfile = new ToolPermissionProfile
        {
            RequiredCapabilities = ToolCapability.None,
            MinimumIsolation = minIsolation
        },
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static Domain.AI.Attestation.ToolExecutionAttestation CreateAttestation(
        string toolName, bool isFailure) => new()
    {
        ToolName = toolName,
        InputHash = "test-hash",
        OutputHash = isFailure ? null : "test-output-hash",
        Timestamp = DateTimeOffset.UtcNow,
        Signature = "test-sig",
        KeyVersion = "v1",
        IsFailureAttestation = isFailure
    };
}
