using Application.AI.Common.Interfaces.Attestation;
using Application.AI.Common.Models.Sandbox;
using Docker.DotNet;
using Docker.DotNet.Models;
using Domain.AI.Attestation;
using Domain.AI.Sandbox;
using FluentAssertions;
using Infrastructure.AI.Sandbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Sandbox;

public class DockerSandboxExecutorTests
{
    private readonly Mock<IDockerClient> _dockerClient = new();
    private readonly Mock<IContainerOperations> _containers = new();
    private readonly Mock<IImageOperations> _images = new();
    private readonly Mock<ISystemOperations> _system = new();
    private readonly Mock<IAttestationService> _attestation = new();
    private readonly Mock<IOptionsMonitor<SandboxOptions>> _options = new();
    private readonly DockerSandboxExecutor _sut;

    public DockerSandboxExecutorTests()
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
            .Setup(x => x.SignAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string tool, string _, string __, CancellationToken ___) =>
                CreateAttestation(tool, false));

        _attestation
            .Setup(x => x.SignFailureAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string tool, string _, string reason, CancellationToken ___) =>
                CreateAttestation(tool, true, reason));

        _options.Setup(x => x.CurrentValue).Returns(new SandboxOptions());

        _sut = new DockerSandboxExecutor(
            _dockerClient.Object,
            _attestation.Object,
            _options.Object,
            Mock.Of<ILogger<DockerSandboxExecutor>>());
    }

    [Fact]
    public async Task ExecuteAsync_SuccessfulExecution_ReturnsOutputAndAttestation()
    {
        var request = CreateRequest();

        var result = await _sut.ExecuteAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Attestation.Should().NotBeNull();
        result.ExitCode.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_Timeout_StopsContainer()
    {
        _containers.Setup(x => x.WaitContainerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromMinutes(5), ct);
                return new ContainerWaitResponse { StatusCode = 0 };
            });

        var request = CreateRequest(timeout: TimeSpan.FromSeconds(1));

        var result = await _sut.ExecuteAsync(request, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("timed out");
        _containers.Verify(x => x.StopContainerAsync(
            "test-container-id", It.IsAny<ContainerStopParameters>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_NetworkNone_DefaultConfig()
    {
        CreateContainerParameters? captured = null;
        _containers.Setup(x => x.CreateContainerAsync(
                It.IsAny<CreateContainerParameters>(), It.IsAny<CancellationToken>()))
            .Callback<CreateContainerParameters, CancellationToken>((p, _) => captured = p)
            .ReturnsAsync(new CreateContainerResponse { ID = "test-id" });

        var request = CreateRequest(capabilities: ToolCapability.FileRead);

        await _sut.ExecuteAsync(request, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.HostConfig.NetworkMode.Should().Be("none");
    }

    [Fact]
    public async Task ExecuteAsync_NetworkAccess_OverridesNetworkMode()
    {
        CreateContainerParameters? captured = null;
        _containers.Setup(x => x.CreateContainerAsync(
                It.IsAny<CreateContainerParameters>(), It.IsAny<CancellationToken>()))
            .Callback<CreateContainerParameters, CancellationToken>((p, _) => captured = p)
            .ReturnsAsync(new CreateContainerResponse { ID = "test-id" });

        var request = CreateRequest(capabilities: ToolCapability.NetworkAccess);

        await _sut.ExecuteAsync(request, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.HostConfig.NetworkMode.Should().Be("bridge");
    }

    [Fact]
    public async Task ExecuteAsync_MemoryLimit_PassedToHostConfig()
    {
        CreateContainerParameters? captured = null;
        _containers.Setup(x => x.CreateContainerAsync(
                It.IsAny<CreateContainerParameters>(), It.IsAny<CancellationToken>()))
            .Callback<CreateContainerParameters, CancellationToken>((p, _) => captured = p)
            .ReturnsAsync(new CreateContainerResponse { ID = "test-id" });

        var request = CreateRequest(memoryBytes: 512 * 1024 * 1024);

        await _sut.ExecuteAsync(request, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.HostConfig.Memory.Should().Be(512 * 1024 * 1024);
    }

    [Fact]
    public async Task ExecuteAsync_ReadonlyRootfs_Enabled()
    {
        CreateContainerParameters? captured = null;
        _containers.Setup(x => x.CreateContainerAsync(
                It.IsAny<CreateContainerParameters>(), It.IsAny<CancellationToken>()))
            .Callback<CreateContainerParameters, CancellationToken>((p, _) => captured = p)
            .ReturnsAsync(new CreateContainerResponse { ID = "test-id" });

        var request = CreateRequest();

        await _sut.ExecuteAsync(request, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.HostConfig.ReadonlyRootfs.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_SecurityHardening_Applied()
    {
        CreateContainerParameters? captured = null;
        _containers.Setup(x => x.CreateContainerAsync(
                It.IsAny<CreateContainerParameters>(), It.IsAny<CancellationToken>()))
            .Callback<CreateContainerParameters, CancellationToken>((p, _) => captured = p)
            .ReturnsAsync(new CreateContainerResponse { ID = "test-id" });

        var request = CreateRequest();

        await _sut.ExecuteAsync(request, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.User.Should().Be("65534:65534");
        captured.HostConfig.CapDrop.Should().Contain("ALL");
        captured.HostConfig.SecurityOpt.Should().Contain("no-new-privileges:true");
    }

    [Fact]
    public async Task ExecuteAsync_DockerUnavailable_MinIsolationContainer_Refuses()
    {
        _system.Setup(x => x.PingAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var request = CreateRequest(minIsolation: SandboxIsolationLevel.Container);

        var result = await _sut.ExecuteAsync(request, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Container isolation required");
        result.Attestation.Should().NotBeNull();
        result.Attestation!.IsFailureAttestation.Should().BeTrue();
        _containers.Verify(x => x.CreateContainerAsync(
            It.IsAny<CreateContainerParameters>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_DockerUnavailable_NoMinIsolation_ReturnsUnavailable()
    {
        _system.Setup(x => x.PingAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var request = CreateRequest(minIsolation: SandboxIsolationLevel.Process);

        var result = await _sut.ExecuteAsync(request, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Docker unavailable");
        result.Attestation.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_WorkspaceMount_BindsCorrectly()
    {
        CreateContainerParameters? captured = null;
        _containers.Setup(x => x.CreateContainerAsync(
                It.IsAny<CreateContainerParameters>(), It.IsAny<CancellationToken>()))
            .Callback<CreateContainerParameters, CancellationToken>((p, _) => captured = p)
            .ReturnsAsync(new CreateContainerResponse { ID = "test-id" });

        var request = CreateRequest();

        await _sut.ExecuteAsync(request, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.HostConfig.Binds.Should().ContainSingle()
            .Which.Should().EndWith(":/workspace:rw");
    }

    [Fact]
    public async Task ExecuteAsync_CommandAndArguments_MappedToCmd()
    {
        CreateContainerParameters? captured = null;
        _containers.Setup(x => x.CreateContainerAsync(
                It.IsAny<CreateContainerParameters>(), It.IsAny<CancellationToken>()))
            .Callback<CreateContainerParameters, CancellationToken>((p, _) => captured = p)
            .ReturnsAsync(new CreateContainerResponse { ID = "test-id" });

        var request = CreateRequest();
        request = request with { Command = "python", Arguments = "script.py" };

        await _sut.ExecuteAsync(request, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.Cmd.Should().BeEquivalentTo(["python", "script.py"]);
    }

    [Fact]
    public async Task ExecuteAsync_ToolOverrideImage_UsesOverrideImage()
    {
        CreateContainerParameters? captured = null;
        _containers.Setup(x => x.CreateContainerAsync(
                It.IsAny<CreateContainerParameters>(), It.IsAny<CancellationToken>()))
            .Callback<CreateContainerParameters, CancellationToken>((p, _) => captured = p)
            .ReturnsAsync(new CreateContainerResponse { ID = "test-id" });

        var options = new SandboxOptions
        {
            ToolOverrides = new Dictionary<string, ToolSandboxOverride>
            {
                ["test_tool"] = new ToolSandboxOverride { ContainerImage = "custom-image:latest" }
            }
        };
        _options.Setup(x => x.CurrentValue).Returns(options);

        var request = CreateRequest();

        await _sut.ExecuteAsync(request, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.Image.Should().Be("custom-image:latest");
    }

    [Fact]
    public async Task ExecuteAsync_ImageNotLocal_PullsImage()
    {
        _images.Setup(x => x.InspectImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DockerImageNotFoundException(System.Net.HttpStatusCode.NotFound, "not found"));

        _images.Setup(x => x.CreateImageAsync(
                It.IsAny<ImagesCreateParameters>(), It.IsAny<AuthConfig>(),
                It.IsAny<IProgress<JSONMessage>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var request = CreateRequest();

        await _sut.ExecuteAsync(request, CancellationToken.None);

        _images.Verify(x => x.CreateImageAsync(
            It.Is<ImagesCreateParameters>(p => p.FromImage == "mcr.microsoft.com/dotnet/runtime:10.0"),
            It.IsAny<AuthConfig>(),
            It.IsAny<IProgress<JSONMessage>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ContainerRemoved_AfterExecution()
    {
        var request = CreateRequest();

        await _sut.ExecuteAsync(request, CancellationToken.None);

        _containers.Verify(x => x.RemoveContainerAsync(
            "test-container-id",
            It.Is<ContainerRemoveParameters>(p => p.Force == true),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static SandboxExecutionRequest CreateRequest(
        ToolCapability capabilities = ToolCapability.None,
        SandboxIsolationLevel minIsolation = SandboxIsolationLevel.Process,
        long memoryBytes = 256 * 1024 * 1024,
        TimeSpan? timeout = null) => new()
    {
        ToolName = "test_tool",
        Input = "{\"action\":\"test\"}",
        Limits = new ResourceLimits { MemoryLimitBytes = memoryBytes },
        PermissionProfile = new ToolPermissionProfile
        {
            RequiredCapabilities = capabilities,
            MinimumIsolation = minIsolation
        },
        Timeout = timeout ?? TimeSpan.FromSeconds(30)
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
