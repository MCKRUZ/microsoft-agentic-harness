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
            return new SandboxSessionTransport(protocolTransport, session);
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
    /// session. Every member below is a pure delegation except <see cref="DisposeAsync"/>.
    /// </summary>
    private sealed class SandboxSessionTransport(ITransport inner, ISandboxSession session) : ITransport
    {
        public string? SessionId => inner.SessionId;

        public System.Threading.Channels.ChannelReader<JsonRpcMessage> MessageReader => inner.MessageReader;

        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken) =>
            inner.SendMessageAsync(message, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await session.DisposeAsync();
        }
    }
}
