using System.IO.Pipelines;
using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.Sandbox;
using Domain.Common;
using FluentAssertions;
using Infrastructure.AI.MCP.Services;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Infrastructure.AI.MCP.Tests.Services;

/// <summary>
/// Coverage for <see cref="SandboxedStdioClientTransport"/> — #371's bridge between a duplex
/// <see cref="ISandboxSession"/> and the MCP SDK's own <see cref="StreamClientTransport"/>. The
/// handshake test runs a REAL <see cref="McpServer"/> on the other end of an in-memory pipe pair
/// standing in for the sandboxed process's stdio, and drives a real <see cref="McpClient"/>
/// through the transport under test — proving the bridge actually carries a working MCP
/// conversation end to end, not just that the streams get assigned to the right properties.
/// </summary>
public class SandboxedStdioClientTransportTests
{
    [Fact]
    public async Task ConnectAsync_SuccessfulSessionStart_CarriesARealMcpConversation()
    {
        var (session, server) = CreatePairedSessionAndServer();
        await using var serverLifetime = server;
        var runTask = server.RunAsync();

        var transport = new SandboxedStdioClientTransport(
            "sandboxed-test-server",
            _ => Task.FromResult(Result<ISandboxSession>.Success((ISandboxSession)session)),
            NullLoggerFactory.Instance);

        await using var client = await McpClient.CreateAsync(
            transport,
            new McpClientOptions { ClientInfo = new() { Name = "test-client", Version = "1.0.0" } },
            NullLoggerFactory.Instance,
            CancellationToken.None);

        var tools = await client.ListToolsAsync();
        tools.Should().ContainSingle(t => t.Name == "echo");

        var result = await client.CallToolAsync(
            "echo", new Dictionary<string, object?> { ["message"] = "hello-from-sandbox" }, cancellationToken: CancellationToken.None);

        result.Content.OfType<TextContentBlock>().First().Text.Should().Contain("hello-from-sandbox");
    }

    [Fact]
    public async Task ConnectAsync_SessionFailsToStart_ThrowsMcpConnectionExceptionNamingTheReason()
    {
        var transport = new SandboxedStdioClientTransport(
            "sandboxed-test-server",
            _ => Task.FromResult(Result<ISandboxSession>.Fail("Docker unavailable")),
            NullLoggerFactory.Instance);

        var act = () => transport.ConnectAsync(CancellationToken.None);

        await act.Should().ThrowAsync<McpConnectionException>()
            .WithMessage("*sandboxed-test-server*")
            .WithMessage("*Docker unavailable*");
    }

    [Fact]
    public async Task DisposeAsync_DisposesTheUnderlyingSandboxSession()
    {
        var (session, server) = CreatePairedSessionAndServer();
        await using var serverLifetime = server;
        var runTask = server.RunAsync();

        var transport = new SandboxedStdioClientTransport(
            "sandboxed-test-server",
            _ => Task.FromResult(Result<ISandboxSession>.Success((ISandboxSession)session)),
            NullLoggerFactory.Instance);

        var protocolTransport = await transport.ConnectAsync(CancellationToken.None);
        session.Disposed.Should().BeFalse("the session must stay alive for the life of the connection");

        await protocolTransport.DisposeAsync();

        session.Disposed.Should().BeTrue(
            "disposing the protocol transport must terminate the sandboxed process/container, not leak it");
    }

    /// <summary>
    /// Wires a fake <see cref="ISandboxSession"/> to a real, live <see cref="McpServer"/> via a pair
    /// of in-memory pipes — the same in-memory-transport pattern the MCP SDK's own samples use for
    /// testing without a child process. This is what makes the handshake test genuine rather than a
    /// mock verifying its own setup.
    /// </summary>
    private static (FakeSandboxSession Session, McpServer Server) CreatePairedSessionAndServer()
    {
        Pipe clientToServer = new(), serverToClient = new();

        var server = McpServer.Create(
            new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()),
            new McpServerOptions
            {
                ServerInfo = new() { Name = "sandboxed-test-server", Version = "1.0.0" },
                ToolCollection = [McpServerTool.Create(
                    (string message) => $"Echo: {message}",
                    new() { Name = "echo" })]
            });

        var session = new FakeSandboxSession(
            standardInput: clientToServer.Writer.AsStream(),
            standardOutput: serverToClient.Reader.AsStream());

        return (session, server);
    }

    private sealed class FakeSandboxSession(Stream standardInput, Stream standardOutput) : ISandboxSession
    {
        public Stream StandardInput { get; } = standardInput;
        public Stream StandardOutput { get; } = standardOutput;
        public Task Completion { get; } = new TaskCompletionSource().Task;
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
