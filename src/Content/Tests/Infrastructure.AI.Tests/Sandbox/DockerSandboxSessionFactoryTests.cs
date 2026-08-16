using System.Text;
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
/// Coverage for <see cref="DockerSandboxSessionFactory"/> — #371's long-lived, duplex
/// counterpart to <see cref="DockerSandboxExecutor"/>, and the genuine isolation boundary among
/// the two session backends. The demultiplexing test constructs a REAL
/// <see cref="MultiplexedStream"/> around hand-crafted Docker stream frames (not a mock of it —
/// <c>MultiplexedStream</c> has no interface to mock) to prove the session actually separates
/// stdout from stderr and delivers only stdout through <see cref="ISandboxSession.StandardOutput"/>,
/// without needing a live Docker daemon.
/// </summary>
public class DockerSandboxSessionFactoryTests
{
    private readonly Mock<IDockerClient> _dockerClient = new();
    private readonly Mock<IContainerOperations> _containers = new();
    private readonly Mock<IImageOperations> _images = new();
    private readonly Mock<ISystemOperations> _system = new();
    private readonly Mock<IOptionsMonitor<SandboxExecutionOptions>> _options = new();
    private readonly Mock<IOptionsMonitor<SandboxConfig>> _sandboxConfig = new();
    private readonly Mock<IAttestationService> _attestation = new();
    private readonly DockerContainerLaunchPreparer _launchPreparer;
    private readonly DockerSandboxSessionFactory _sut;
    private CreateContainerParameters? _capturedParams;

    public DockerSandboxSessionFactoryTests()
    {
        _dockerClient.Setup(x => x.Containers).Returns(_containers.Object);
        _dockerClient.Setup(x => x.System).Returns(_system.Object);
        _dockerClient.Setup(x => x.Images).Returns(_images.Object);

        _system.Setup(x => x.PingAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _images.Setup(x => x.InspectImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ImageInspectResponse());

        _containers.Setup(x => x.CreateContainerAsync(
                It.IsAny<CreateContainerParameters>(), It.IsAny<CancellationToken>()))
            .Callback<CreateContainerParameters, CancellationToken>((p, _) => _capturedParams = p)
            .ReturnsAsync(new CreateContainerResponse { ID = "test-container-id" });

        _containers.Setup(x => x.StartContainerAsync(
                It.IsAny<string>(), It.IsAny<ContainerStartParameters>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _containers.Setup(x => x.RemoveContainerAsync(
                It.IsAny<string>(), It.IsAny<ContainerRemoveParameters>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Container never "exits" during these tests unless a test explicitly cancels it —
        // sessions are long-lived by design, so WaitContainerAsync should stay pending.
        _containers.Setup(x => x.WaitContainerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((_, ct) => Task.Delay(Timeout.Infinite, ct)
                .ContinueWith(_ => new ContainerWaitResponse(), TaskScheduler.Default));

        _options.Setup(x => x.CurrentValue).Returns(new SandboxExecutionOptions());
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

        _launchPreparer = new DockerContainerLaunchPreparer(
            _dockerClient.Object, _options.Object, Mock.Of<ILogger<DockerContainerLaunchPreparer>>());

        _sut = new DockerSandboxSessionFactory(
            _dockerClient.Object,
            _launchPreparer,
            new SandboxEgressPreflightRunner(null, Mock.Of<ILogger<SandboxEgressPreflightRunner>>()),
            new SandboxSessionRejectionSigner(_attestation.Object),
            _sandboxConfig.Object,
            Mock.Of<ILogger<DockerSandboxSession>>());
    }

    [Fact]
    public async Task StartSessionAsync_Success_OpensInteractiveStdioOnContainer()
    {
        SetUpAttachStream(BuildFrames(("stdout", "ready")));

        var result = await _sut.StartSessionAsync(CreateRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        await using var session = result.Value!;

        // The one-shot executor never sets these (its input goes in via a bind-mounted file) —
        // this is the shape unique to a session that needs a live stdio conversation.
        _capturedParams.Should().NotBeNull();
        _capturedParams!.OpenStdin.Should().BeTrue();
        _capturedParams.AttachStdin.Should().BeTrue();
        _capturedParams.AttachStdout.Should().BeTrue();
        _capturedParams.AttachStderr.Should().BeTrue();
        _capturedParams.Tty.Should().BeFalse("Tty must stay false for the attach stream to be demultiplexed");
    }

    [Fact]
    public async Task StartSessionAsync_StandardOutput_ContainsOnlyDemultiplexedStdout()
    {
        SetUpAttachStream(BuildFrames(
            ("stdout", "hello "),
            ("stderr", "SHOULD-NOT-APPEAR"),
            ("stdout", "world")));

        var result = await _sut.StartSessionAsync(CreateRequest(), CancellationToken.None);
        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        await using var session = result.Value!;

        using var reader = new StreamReader(session.StandardOutput);
        var output = await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5));

        output.Should().Be("hello world");
    }

    [Fact]
    public async Task StartSessionAsync_StandardInput_WritesReachTheAttachStreamUnframed()
    {
        var fakeStream = SetUpAttachStream(BuildFrames(("stdout", "")));

        var result = await _sut.StartSessionAsync(CreateRequest(), CancellationToken.None);
        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        await using var session = result.Value!;

        await session.StandardInput.WriteAsync(Encoding.UTF8.GetBytes("ping"));
        await session.StandardInput.FlushAsync();

        // Docker does not multiplex the write direction — stdin bytes should reach the
        // transport raw, with no 8-byte frame header prepended.
        Encoding.UTF8.GetString(fakeStream.WrittenBytes.ToArray()).Should().Be("ping");
    }

    [Fact]
    public async Task StartSessionAsync_SandboxDisabled_FailsWithoutCreatingContainer()
    {
        _sandboxConfig.Setup(x => x.CurrentValue).Returns(new SandboxConfig { Enabled = false });

        var result = await _sut.StartSessionAsync(CreateRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        _containers.Verify(x => x.CreateContainerAsync(
            It.IsAny<CreateContainerParameters>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartSessionAsync_NonPositiveCpuLimit_FailsWithoutCreatingContainer()
    {
        var request = CreateRequest() with { Limits = new ResourceLimits { CpuCoreLimit = 0 } };

        var result = await _sut.StartSessionAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Contains("CpuCoreLimit"));
        _containers.Verify(x => x.CreateContainerAsync(
            It.IsAny<CreateContainerParameters>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("LD_PRELOAD")]
    [InlineData("ld_library_path")]
    [InlineData("LD_AUDIT")]
    public async Task StartSessionAsync_DynamicLinkerHijackEnvironmentGrant_FailsWithoutCreatingContainer(string grantName)
    {
        // A request with FileWrite gets a read-write bind mount at /workspace — an unguarded
        // LD_PRELOAD/LD_LIBRARY_PATH/LD_AUDIT grant would let a caller write a malicious .so there
        // and have the container's own dynamic linker load it into the sandboxed process on start.
        var request = CreateRequest() with
        {
            EnvironmentVariables = new Dictionary<string, string> { [grantName] = "/workspace/evil.so" }
        };

        var result = await _sut.StartSessionAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Contains("dynamic-linker-hijack"));
        _containers.Verify(x => x.CreateContainerAsync(
            It.IsAny<CreateContainerParameters>(), It.IsAny<CancellationToken>()), Times.Never);
        _attestation.Verify(x => x.SignAsync(
            It.Is<AttestationRequest>(r => r.IsFailure), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartSessionAsync_DockerUnavailable_ContainerRequired_FailsWithoutDowngrading()
    {
        _system.Setup(x => x.PingAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("daemon unreachable"));

        var request = CreateRequest() with
        {
            PermissionProfile = new ToolPermissionProfile
            {
                RequiredCapabilities = ToolCapability.None,
                AllowedPrograms = ["mcp-server"],
                MinimumIsolation = SandboxIsolationLevel.Container
            }
        };

        var result = await _sut.StartSessionAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Contains("Cannot downgrade"));
    }

    [Fact]
    public async Task StartSessionAsync_Success_PassesEnvironmentVariablesToTheContainer()
    {
        SetUpAttachStream(BuildFrames(("stdout", "")));
        var request = CreateRequest() with
        {
            EnvironmentVariables = new Dictionary<string, string> { ["API_KEY"] = "secret-value" }
        };

        var result = await _sut.StartSessionAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        await using var session = result.Value!;
        _capturedParams.Should().NotBeNull();
        _capturedParams!.Env.Should().Contain("API_KEY=secret-value",
            "a bundle-owned MCP server that needs an API key/config via env var must actually receive it");
    }

    [Fact]
    public async Task StartSessionAsync_NullCommand_FallsBackToToolName()
    {
        SetUpAttachStream(BuildFrames(("stdout", "")));
        var request = CreateRequest() with { Command = null, ToolName = "my-mcp-tool" };

        var result = await _sut.StartSessionAsync(request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(string.Join("; ", result.Errors));
        await using var session = result.Value!;
        _capturedParams!.Cmd.Should().BeEquivalentTo(["my-mcp-tool"]);
    }

    [Fact]
    public async Task StartSessionAsync_NonPositiveCpuLimit_SignsFailureAttestation()
    {
        var request = CreateRequest() with { Limits = new ResourceLimits { CpuCoreLimit = 0 } };

        await _sut.StartSessionAsync(request, CancellationToken.None);

        _attestation.Verify(x => x.SignAsync(
            It.Is<AttestationRequest>(r => r.IsFailure && r.ToolName == request.ToolName),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartSessionAsync_ContainerCreateThrows_SignsFailureAttestationAndCleansUpWorkspace()
    {
        _containers.Setup(x => x.CreateContainerAsync(
                It.IsAny<CreateContainerParameters>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await _sut.StartSessionAsync(CreateRequest(), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        _attestation.Verify(x => x.SignAsync(
            It.Is<AttestationRequest>(r => r.IsFailure), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartSessionAsync_CallerCancels_PropagatesOperationCanceledInsteadOfResultFail()
    {
        using var cts = new CancellationTokenSource();
        _containers.Setup(x => x.CreateContainerAsync(
                It.IsAny<CreateContainerParameters>(), It.IsAny<CancellationToken>()))
            .Returns<CreateContainerParameters, CancellationToken>((_, ct) =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(new CreateContainerResponse());
            });

        var act = () => _sut.StartSessionAsync(CreateRequest(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "a caller-initiated cancellation must propagate as a cancellation, not be converted into an ordinary Result.Fail");
    }

    private FakeAttachStream SetUpAttachStream(byte[] frames)
    {
        var fakeStream = new FakeAttachStream(frames);
        var multiplexed = new MultiplexedStream(fakeStream, multiplexed: true);

        _containers.Setup(x => x.AttachContainerAsync(
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<ContainerAttachParameters>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(multiplexed);

        return fakeStream;
    }

    /// <summary>
    /// Encodes frames using Docker's documented multiplexed stream wire format: an 8-byte header
    /// per frame — <c>[STREAM_TYPE, 0, 0, 0, SIZE1, SIZE2, SIZE3, SIZE4]</c>, stream type 1 =
    /// stdout / 2 = stderr, size as a big-endian uint32 — followed by that many payload bytes.
    /// </summary>
    private static byte[] BuildFrames(params (string target, string text)[] chunks)
    {
        using var buffer = new MemoryStream();
        foreach (var (target, text) in chunks)
        {
            byte streamType = target switch
            {
                "stdout" => 1,
                "stderr" => 2,
                _ => throw new ArgumentOutOfRangeException(nameof(chunks), target, "expected stdout or stderr")
            };

            var payload = Encoding.UTF8.GetBytes(text);
            buffer.WriteByte(streamType);
            buffer.WriteByte(0);
            buffer.WriteByte(0);
            buffer.WriteByte(0);

            var lengthBytes = BitConverter.GetBytes((uint)payload.Length);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(lengthBytes);
            buffer.Write(lengthBytes, 0, 4);
            buffer.Write(payload, 0, payload.Length);
        }

        return buffer.ToArray();
    }

    private static SandboxSessionRequest CreateRequest() => new()
    {
        ToolName = "test_session_tool",
        Limits = new ResourceLimits(),
        PermissionProfile = new ToolPermissionProfile
        {
            RequiredCapabilities = ToolCapability.None,
            AllowedPrograms = ["mcp-server"]
        },
        Command = "mcp-server",
        MaxSessionDuration = TimeSpan.FromSeconds(30)
    };

    /// <summary>
    /// A minimal duplex <see cref="Stream"/> standing in for the live Docker attach connection:
    /// reads are served from a pre-loaded buffer of crafted frames (naturally EOFs once
    /// exhausted, which is what lets <see cref="MultiplexedStream.ReadOutputAsync"/> observe
    /// the session's end), writes are captured for inspection rather than sent anywhere.
    /// </summary>
    private sealed class FakeAttachStream : Stream
    {
        private readonly MemoryStream _readSource;

        public FakeAttachStream(byte[] readBytes) => _readSource = new MemoryStream(readBytes);

        public MemoryStream WrittenBytes { get; } = new();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _readSource.Read(buffer, offset, count);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _readSource.ReadAsync(buffer, offset, count, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => WrittenBytes.Write(buffer, offset, count);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WrittenBytes.WriteAsync(buffer, offset, count, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
