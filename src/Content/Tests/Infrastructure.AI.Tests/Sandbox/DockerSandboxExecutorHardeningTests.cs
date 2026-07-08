using Application.AI.Common.Interfaces.Attestation;
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
/// Security regression coverage for sandbox audit finding A6-3: <see cref="DockerSandboxExecutor"/>
/// leaked running containers when the caller's token was cancelled (cleanup used the already
/// cancelled token) and applied no CPU limit to sandboxed containers. These tests assert
/// cleanup survives cancellation and CPU limits reach the Docker host config.
/// </summary>
public class DockerSandboxExecutorHardeningTests
{
    private readonly Mock<IDockerClient> _dockerClient = new();
    private readonly Mock<IContainerOperations> _containers = new();
    private readonly Mock<IImageOperations> _images = new();
    private readonly Mock<ISystemOperations> _system = new();
    private readonly Mock<IAttestationService> _attestation = new();
    private readonly Mock<IOptionsMonitor<SandboxExecutionOptions>> _options = new();
    private readonly DockerSandboxExecutor _sut;

    public DockerSandboxExecutorHardeningTests()
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

        _attestation
            .Setup(x => x.SignAsync(It.IsAny<AttestationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AttestationRequest r, CancellationToken _) =>
                CreateAttestation(r.ToolName, r.IsFailure, r.FailureReason));

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
    public async Task ExecuteAsync_ExternalCancellation_StillRemovesContainer()
    {
        using var externalCts = new CancellationTokenSource();

        _containers.Setup(x => x.WaitContainerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(async (_, ct) =>
            {
                externalCts.CancelAfter(TimeSpan.FromMilliseconds(50));
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new ContainerWaitResponse { StatusCode = 0 };
            });

        // Mimic the real Docker client: a call made with an already cancelled token
        // throws immediately and never reaches the daemon.
        var removed = false;
        _containers.Setup(x => x.RemoveContainerAsync(
                It.IsAny<string>(), It.IsAny<ContainerRemoveParameters>(), It.IsAny<CancellationToken>()))
            .Returns<string, ContainerRemoveParameters, CancellationToken>((_, _, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                removed = true;
                return Task.CompletedTask;
            });

        var act = () => _sut.ExecuteAsync(CreateRequest(), externalCts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        removed.Should().BeTrue(
            "a cancelled execution must still force-remove its container — otherwise the container keeps running unbounded on the host");
    }

    [Fact]
    public async Task ExecuteAsync_CpuLimit_PassedToHostConfigAsNanoCpus()
    {
        CreateContainerParameters? captured = null;
        _containers.Setup(x => x.CreateContainerAsync(
                It.IsAny<CreateContainerParameters>(), It.IsAny<CancellationToken>()))
            .Callback<CreateContainerParameters, CancellationToken>((p, _) => captured = p)
            .ReturnsAsync(new CreateContainerResponse { ID = "test-id" });

        var request = CreateRequest() with
        {
            Limits = new ResourceLimits { CpuCoreLimit = 2.0 }
        };

        await _sut.ExecuteAsync(request, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.HostConfig.NanoCPUs.Should().Be(2_000_000_000L,
            "the sandbox must cap container CPU alongside memory — an unlimited container can starve the host");
    }

    [Fact]
    public async Task ExecuteAsync_DefaultCpuLimit_OneCore()
    {
        CreateContainerParameters? captured = null;
        _containers.Setup(x => x.CreateContainerAsync(
                It.IsAny<CreateContainerParameters>(), It.IsAny<CancellationToken>()))
            .Callback<CreateContainerParameters, CancellationToken>((p, _) => captured = p)
            .ReturnsAsync(new CreateContainerResponse { ID = "test-id" });

        await _sut.ExecuteAsync(CreateRequest(), CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.HostConfig.NanoCPUs.Should().Be(1_000_000_000L,
            "closed-by-default: a request that does not opt into more CPU gets one core");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public async Task ExecuteAsync_NonPositiveCpuLimit_RejectsRequestInsteadOfRunningUnlimited(double cpuCoreLimit)
    {
        // NanoCPUs = 0 means "unlimited" to Docker, so a zero/negative CpuCoreLimit must be
        // rejected as invalid rather than silently granting the container the whole host.
        var request = CreateRequest() with
        {
            Limits = new ResourceLimits { CpuCoreLimit = cpuCoreLimit }
        };

        var result = await _sut.ExecuteAsync(request, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("CpuCoreLimit");
        result.Attestation.Should().NotBeNull("the rejection must leave a signed audit record");
        result.Attestation!.IsFailureAttestation.Should().BeTrue();
        _containers.Verify(x => x.CreateContainerAsync(
            It.IsAny<CreateContainerParameters>(), It.IsAny<CancellationToken>()), Times.Never);
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

    private static ToolExecutionAttestation CreateAttestation(
        string toolName, bool isFailure, string? reason = null) => new()
    {
        ToolName = toolName,
        InputHash = "test-hash",
        OutputHash = isFailure ? null : "test-output-hash",
        Timestamp = DateTimeOffset.UtcNow,
        Signature = "test-sig",
        KeyVersion = "v1",
        IsFailureAttestation = isFailure,
        FailureReason = reason
    };
}
