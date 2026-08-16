using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Text;
using Application.AI.Common.Interfaces.Sandbox;
using Docker.DotNet;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Sandbox;

/// <summary>
/// A Docker-backed <see cref="ISandboxSession"/>: a genuine isolation boundary (unprivileged
/// user, dropped capabilities, read-only root filesystem, no network unless granted), unlike
/// <see cref="ProcessSandboxSession"/>. Adapts Docker.DotNet's <see cref="MultiplexedStream"/> —
/// which is not itself a <see cref="Stream"/> and interleaves stdout/stderr with a framing
/// header — into the plain duplex <see cref="Stream"/> pair <see cref="ISandboxSession"/>
/// exposes: a write-through wrapper for stdin, and a background pump that demultiplexes stdout
/// into a <see cref="Pipe"/> (stderr is logged, not exposed — see #371).
/// </summary>
public sealed class DockerSandboxSession : ISandboxSession
{
    private readonly IDockerClient _dockerClient;
    private readonly DockerContainerLaunchPreparer _launchPreparer;
    private readonly MultiplexedStream _attachStream;
    private readonly string _containerId;
    private readonly string _toolName;
    private readonly string _workspaceDir;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetimeCts;
    private readonly Pipe _outputPipe = new();
    private readonly Task _waitForExit;
    private readonly Task _outputPump;
    private readonly Stream _standardOutput;
    private int _disposed;

    internal DockerSandboxSession(
        IDockerClient dockerClient,
        DockerContainerLaunchPreparer launchPreparer,
        MultiplexedStream attachStream,
        string containerId,
        string toolName,
        string workspaceDir,
        TimeSpan maxSessionDuration,
        ILogger logger)
    {
        _dockerClient = dockerClient;
        _launchPreparer = launchPreparer;
        _attachStream = attachStream;
        _containerId = containerId;
        _toolName = toolName;
        _workspaceDir = workspaceDir;
        _logger = logger;
        _lifetimeCts = new CancellationTokenSource(maxSessionDuration);

        StandardInput = new MultiplexedWriteStream(attachStream);
        _standardOutput = _outputPipe.Reader.AsStream();

        _outputPump = PumpOutputAsync(_lifetimeCts.Token);
        _waitForExit = WaitForExitAsync();
    }

    /// <inheritdoc />
    public Stream StandardInput { get; }

    /// <inheritdoc />
    public Stream StandardOutput => _standardOutput;

    /// <inheritdoc />
    public Task Completion => _waitForExit;

    private async Task WaitForExitAsync()
    {
        try
        {
            await _dockerClient.Containers.WaitContainerAsync(_containerId, _lifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Either MaxSessionDuration elapsed or DisposeAsync requested an early stop. Stopping
            // the container also closes the attach stream, which is what unblocks the read loop
            // in PumpOutputAsync below — DisposeAsync's await order depends on that.
            await _launchPreparer.StopContainerGracefullyAsync(_containerId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected failure waiting for sandboxed container {ContainerId} to exit", _containerId);
        }
    }

    private async Task PumpOutputAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                var result = await _attachStream.ReadOutputAsync(buffer, 0, buffer.Length, ct);

                if (result.Count > 0)
                {
                    if (result.Target == MultiplexedStream.TargetStream.StandardOut)
                    {
                        await _outputPipe.Writer.WriteAsync(buffer.AsMemory(0, result.Count), ct);
                    }
                    else if (result.Target == MultiplexedStream.TargetStream.StandardError && _logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug("{ToolName} sandboxed session stderr: {Chunk}",
                            _toolName, Encoding.UTF8.GetString(buffer, 0, result.Count));
                    }
                }

                if (result.EOF)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Session disposed or lifetime elapsed before the container produced EOF on its own.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Docker sandbox output pump failed for {ToolName}", _toolName);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            await _outputPipe.Writer.CompleteAsync();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        await _lifetimeCts.CancelAsync();
        // Order matters: awaiting the container-exit wait first is what stops the container
        // (see WaitForExitAsync's catch), which closes the attach stream and unblocks the
        // pump's in-flight read — awaiting the pump before this would deadlock if the
        // underlying transport does not itself honor the cancellation token.
        await _waitForExit;
        await _outputPump;

        await _launchPreparer.RemoveContainerSafeAsync(_containerId);
        _launchPreparer.CleanupWorkspace(_workspaceDir);

        SafeDispose(StandardInput, "stdin stream");
        SafeDispose(_standardOutput, "stdout stream");
        SafeDispose(_attachStream, "attach stream");
        SafeDispose(_lifetimeCts, "lifetime cancellation token source");
    }

    private void SafeDispose(IDisposable disposable, string what)
    {
        try
        {
            disposable.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to dispose {What} during sandbox session teardown for {ToolName}", what, _toolName);
        }
    }

    /// <summary>
    /// Adapts <see cref="MultiplexedStream"/>'s byte-array write method to a plain writable
    /// <see cref="Stream"/> — the shape the MCP SDK's <c>StreamClientTransport</c> expects.
    /// Read is not supported: output flows through the demultiplexed <see cref="Pipe"/> in
    /// <see cref="DockerSandboxSession"/> instead. Disposing signals stdin EOF via
    /// <see cref="MultiplexedStream.CloseWrite"/> without disposing the shared underlying
    /// stream — that happens once, in <see cref="DockerSandboxSession.DisposeAsync"/>, after
    /// both directions are done with it.
    /// </summary>
    private sealed class MultiplexedWriteStream(MultiplexedStream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("Sandboxed session output is read via ISandboxSession.StandardOutput, not this stream.");

        // Stream's Write(byte[], int, int) is abstract, so a sync override is required even
        // though the MCP SDK's writer only ever calls the async path below — this exists purely
        // to satisfy the base class contract for a hypothetical sync caller, and blocking here is
        // the only option short of throwing NotSupportedException (which would break any such
        // caller outright rather than merely being slow).
        public override void Write(byte[] buffer, int offset, int count) =>
            inner.WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.WriteAsync(buffer, offset, count, cancellationToken);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // MultiplexedStream only exposes a byte[]-based WriteAsync overload. Most callers
            // (including the MCP SDK's JSON-RPC framing) hand this an array-backed buffer, so
            // TryGetArray lets it dispatch straight through with no extra copy — exactly what
            // Stream's own default WriteAsync(Memory<byte>) implementation does internally. Only
            // a genuinely non-array-backed buffer (native/pinned memory) pays for a rent+copy.
            if (MemoryMarshal.TryGetArray(buffer, out var segment))
            {
                await inner.WriteAsync(segment.Array!, segment.Offset, segment.Count, cancellationToken);
                return;
            }

            var rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                buffer.CopyTo(rented);
                await inner.WriteAsync(rented, 0, buffer.Length, cancellationToken);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.CloseWrite();
            base.Dispose(disposing);
        }
    }
}
