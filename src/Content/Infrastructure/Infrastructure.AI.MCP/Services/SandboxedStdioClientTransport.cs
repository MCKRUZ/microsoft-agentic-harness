using Application.AI.Common.Interfaces.Sandbox;
using Domain.Common;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Infrastructure.AI.MCP.Services;

/// <summary>
/// An <see cref="IClientTransport"/> for a bundle-owned stdio MCP server whose process runs
/// inside the harness's sandbox rather than launching directly on the host — see #371. Starts a
/// duplex <see cref="ISandboxSession"/> and hands its two streams to the MCP SDK's own
/// <see cref="StreamClientTransport"/>, so the JSON-RPC framing and protocol handling need no
/// changes; this type is purely the bridge between a sandbox session and the SDK's stream-based
/// transport.
/// </summary>
/// <remarks>
/// Deliberately narrow: it knows nothing about isolation levels, resource limits, or bundle
/// policy — it is handed a <paramref name="startSession"/> delegate that already knows how to
/// start the right kind of session, so this type can be unit-tested with a fake session and
/// stays reusable regardless of how <c>McpConnectionManager</c> decides to build that request.
/// </remarks>
public sealed class SandboxedStdioClientTransport(
    string serverName,
    Func<CancellationToken, Task<Result<ISandboxSession>>> startSession,
    ILoggerFactory loggerFactory) : IClientTransport
{
    /// <inheritdoc />
    public string Name => serverName;

    /// <inheritdoc />
    public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var result = await startSession(cancellationToken);
        if (!result.IsSuccess)
        {
            throw new Application.AI.Common.Exceptions.McpConnectionException(
                $"Sandboxed stdio MCP server '{serverName}' failed to start: {string.Join("; ", result.Errors)}");
        }

        var session = result.Value!;
        try
        {
            var streamTransport = new StreamClientTransport(session.StandardInput, session.StandardOutput, loggerFactory);
            var protocolTransport = await streamTransport.ConnectAsync(cancellationToken);
            return new SandboxSessionTransport(
                protocolTransport, session, serverName, loggerFactory.CreateLogger<SandboxedStdioClientTransport>());
        }
        catch
        {
            // The protocol handshake never got far enough to own the session's lifetime —
            // this type must tear it down itself rather than leak the sandboxed process/container.
            await session.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Wraps the SDK's own connected <see cref="ITransport"/> so that disposing it also disposes
    /// the underlying <see cref="ISandboxSession"/> — terminating the sandboxed process/container
    /// and releasing its resources — in the same place the SDK already tears down the protocol
    /// session, and so an unexpected mid-session exit (crash, OOM-kill) is logged rather than
    /// surfacing only as a delayed stream EOF the SDK's own read loop eventually notices. Every
    /// member below is a pure delegation except <see cref="DisposeAsync"/>.
    /// </summary>
    private sealed class SandboxSessionTransport : ITransport
    {
        private readonly ITransport _inner;
        private readonly ISandboxSession _session;
        private readonly string _serverName;
        private readonly ILogger _logger;
        private int _disposed;

        public SandboxSessionTransport(ITransport inner, ISandboxSession session, string serverName, ILogger logger)
        {
            _inner = inner;
            _session = session;
            _serverName = serverName;
            _logger = logger;
            _ = ObserveUnexpectedExitAsync();
        }

        public string? SessionId => _inner.SessionId;

        public System.Threading.Channels.ChannelReader<JsonRpcMessage> MessageReader => _inner.MessageReader;

        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken) =>
            _inner.SendMessageAsync(message, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _disposed, 1);
            await _inner.DisposeAsync();
            await _session.DisposeAsync();
        }

        /// <summary>
        /// <see cref="ISandboxSession.Completion"/> is guaranteed to complete once disposed, so this
        /// only logs when it completes BEFORE disposal — an unexpected exit while the conversation
        /// was still in progress, not the ordinary teardown path.
        /// </summary>
        private async Task ObserveUnexpectedExitAsync()
        {
            try
            {
                await _session.Completion;
            }
            catch
            {
                // Completion is documented to never fault; guard defensively anyway since this is a
                // fire-and-forget observer with no caller to propagate a fault to.
            }

            if (Volatile.Read(ref _disposed) == 0)
            {
                _logger.LogWarning(
                    "Sandboxed stdio MCP server '{ServerName}' exited unexpectedly mid-session.", _serverName);
            }
        }
    }
}
