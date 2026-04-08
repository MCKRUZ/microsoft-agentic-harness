using System.Collections.Concurrent;
using Application.AI.Common.Exceptions;
using Domain.Common.Config.AI.MCP;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Infrastructure.AI.MCP.Services;

/// <summary>
/// Manages the lifecycle of MCP client connections. Creates, caches, and
/// disposes <see cref="McpClient"/> instances for each configured server.
/// </summary>
/// <remarks>
/// <para>
/// Connections are lazily initialized on first access and cached for reuse.
/// Failed connections throw <see cref="McpConnectionException"/> with the
/// server name and transport type for structured error handling.
/// </para>
/// </remarks>
public sealed class McpConnectionManager : IAsyncDisposable
{
    private readonly ILogger<McpConnectionManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly McpServersConfig _config;
    private readonly ConcurrentDictionary<string, McpClient> _clients = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _connectionLocks = new();
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="McpConnectionManager"/> class.
    /// </summary>
    public McpConnectionManager(
        ILogger<McpConnectionManager> logger,
        ILoggerFactory loggerFactory,
        McpServersConfig config)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _config = config;
    }

    /// <summary>
    /// Gets or creates an MCP client connection for the specified server.
    /// </summary>
    /// <param name="serverName">The server name from configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A connected MCP client.</returns>
    /// <exception cref="McpConnectionException">Thrown when connection fails.</exception>
    public async Task<McpClient> GetClientAsync(string serverName, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_clients.TryGetValue(serverName, out var existing))
            return existing;

        var connectionLock = _connectionLocks.GetOrAdd(serverName, _ => new SemaphoreSlim(1, 1));
        await connectionLock.WaitAsync(cancellationToken);

        try
        {
            if (_clients.TryGetValue(serverName, out existing))
                return existing;

            var client = await CreateClientAsync(serverName, cancellationToken);
            _clients[serverName] = client;
            return client;
        }
        finally
        {
            connectionLock.Release();
        }
    }

    /// <summary>
    /// Checks if a server connection is active and healthy.
    /// </summary>
    public bool IsConnected(string serverName)
    {
        return _clients.ContainsKey(serverName);
    }

    /// <summary>
    /// Disconnects from a specific server and removes the cached connection.
    /// </summary>
    public async Task DisconnectAsync(string serverName)
    {
        if (_clients.TryRemove(serverName, out var client))
        {
            await client.DisposeAsync();
            _logger.LogInformation("Disconnected from MCP server '{ServerName}'", serverName);
        }
    }

    /// <summary>
    /// Gets the names of all configured and enabled servers.
    /// </summary>
    public IEnumerable<string> GetConfiguredServerNames()
    {
        return _config.Servers
            .Where(kvp => kvp.Value.Enabled)
            .Select(kvp => kvp.Key);
    }

    private async Task<McpClient> CreateClientAsync(string serverName, CancellationToken cancellationToken)
    {
        if (!_config.Servers.TryGetValue(serverName, out var definition))
            throw new McpConnectionException($"MCP server '{serverName}' is not configured.");

        if (!definition.Enabled)
            throw new McpConnectionException($"MCP server '{serverName}' is disabled.");

        _logger.LogInformation(
            "Connecting to MCP server '{ServerName}' via {Transport}...",
            serverName, definition.Type);

        try
        {
            var transport = CreateTransport(serverName, definition);

            var client = await McpClient.CreateAsync(
                transport,
                new McpClientOptions
                {
                    ClientInfo = new() { Name = "agentic-harness", Version = "1.0.0" },
                    InitializationTimeout = TimeSpan.FromSeconds(definition.StartupTimeoutSeconds)
                },
                _loggerFactory,
                cancellationToken);

            _logger.LogInformation("Connected to MCP server '{ServerName}'", serverName);
            return client;
        }
        catch (Exception ex) when (ex is not McpConnectionException)
        {
            throw new McpConnectionException(serverName, definition.Type.ToString().ToLowerInvariant(), ex);
        }
    }

    private static IClientTransport CreateTransport(string serverName, McpServerDefinition definition)
    {
        return definition.Type switch
        {
            McpServerType.Stdio => new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = serverName,
                Command = definition.Command,
                Arguments = definition.Args,
                WorkingDirectory = definition.WorkingDirectory,
                EnvironmentVariables = definition.Env.Count > 0
                    ? definition.Env.ToDictionary(kvp => kvp.Key, kvp => (string?)kvp.Value)
                    : null
            }),
            McpServerType.Http or McpServerType.Sse => CreateHttpTransport(serverName, definition),
            _ => throw new McpConnectionException($"Unsupported MCP transport type: {definition.Type}")
        };
    }

    private static HttpClientTransport CreateHttpTransport(string serverName, McpServerDefinition definition)
    {
        var options = new HttpClientTransportOptions
        {
            Name = serverName,
            Endpoint = new Uri(definition.Url ?? throw new McpConnectionException(
                $"MCP server '{serverName}' is configured as {definition.Type} but has no URL."))
        };

        // Apply auth headers from configuration
        if (definition.Auth is { IsConfigured: true, IsValid: true } auth)
        {
            options.AdditionalHeaders = auth.Type switch
            {
                McpServerAuthType.ApiKey => new Dictionary<string, string>
                {
                    [auth.ApiKeyHeader] = auth.ApiKey!
                },
                McpServerAuthType.Bearer => new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {auth.BearerToken}"
                },
                _ => null
            };
        }

        return new HttpClientTransport(options);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var kvp in _clients)
        {
            await kvp.Value.DisposeAsync();
        }

        _clients.Clear();

        foreach (var kvp in _connectionLocks)
        {
            kvp.Value.Dispose();
        }

        _connectionLocks.Clear();
    }
}
