using System.Collections.Concurrent;
using Application.AI.Common.Exceptions;
using Domain.Common.Config.AI.MCP;
using Infrastructure.AI.Egress;
using Infrastructure.AI.Identity;
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
    private readonly HttpMessageHandler _antiSsrfHandler;
    private readonly HttpClient _httpClient;
    private readonly McpServersConfig _config;
    private readonly ConcurrentDictionary<string, McpClient> _clients = new();
    private readonly ConcurrentDictionary<string, HttpClient> _entraClients = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _connectionLocks = new();
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="McpConnectionManager"/> class.
    /// </summary>
    /// <remarks>
    /// The SSRF defense is a hard dependency, not a configuration option: the single
    /// shared <see cref="HttpClient"/> used for every HTTP/SSE transport is built on the
    /// <c>AntiSSRFHandler</c> produced by <paramref name="antiSsrfHandlerFactory"/>, which
    /// performs connect-time IP filtering (RFC 1918, loopback, link-local, IMDS, IPv6 ULA)
    /// and redirect re-validation. There is no code path that constructs an unguarded
    /// client, so SSRF protection cannot be silently omitted by misconfiguration.
    /// <para>
    /// This deliberately applies only the AntiSSRF ring, NOT the outer
    /// <c>EgressPolicyDelegatingHandler</c> (per-skill hostname allowlist + JSONL audit)
    /// that the general egress <see cref="HttpClient"/> composes. MCP servers are
    /// explicitly admin-configured, and connections are established outside an agent turn
    /// (e.g. startup tool discovery) where that handler's required agent identity is
    /// absent — it would deny every connection. SSRF filtering, the security-critical
    /// ring, applies unconditionally.
    /// </para>
    /// </remarks>
    public McpConnectionManager(
        ILogger<McpConnectionManager> logger,
        ILoggerFactory loggerFactory,
        AntiSsrfHandlerFactory antiSsrfHandlerFactory,
        McpServersConfig config)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        // The shared SSRF-guard terminal handler. Captured so Entra connections can wrap
        // it with a per-server token-injecting handler while still routing through the
        // same connect-time IP filter.
        _antiSsrfHandler = antiSsrfHandlerFactory.GetOrCreate();
        // disposeHandler: false — the AntiSSRF handler is a shared singleton owned by
        // the factory; this client must not dispose it.
        _httpClient = new HttpClient(_antiSsrfHandler, disposeHandler: false);
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

        // Release the per-server Entra client (and its pooled sockets) on explicit
        // disconnect, mirroring the cleanup of _clients above. disposeHandler:false leaves
        // the shared AntiSSRF handler intact; a later reconnect recreates a fresh
        // token-injecting client.
        if (_entraClients.TryRemove(serverName, out var entraClient))
        {
            entraClient.Dispose();
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

    private IClientTransport CreateTransport(string serverName, McpServerDefinition definition)
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

    private HttpClientTransport CreateHttpTransport(string serverName, McpServerDefinition definition)
    {
        var uri = new Uri(definition.Url ?? throw new McpConnectionException(
            $"MCP server '{serverName}' is configured as {definition.Type} but has no URL."));

        ValidateMcpServerUrl(uri, serverName);

        var options = new HttpClientTransportOptions
        {
            Name = serverName,
            Endpoint = uri
        };

        // Select the HTTP client carrying the right credential. Static schemes (ApiKey,
        // Bearer) reuse the shared SSRF-guarded client with per-request headers; Entra
        // gets a per-server client whose token-injecting handler mints a fresh, rotating
        // token in front of the same SSRF guard. All paths still route through the
        // connect-time IP filter, so a URL resolving to an internal/metadata address is
        // refused at the socket. The transport does not own the supplied client.
        var httpClient = ResolveTransportHttpClient(serverName, definition.Auth, options);

        return new HttpClientTransport(options, httpClient, _loggerFactory);
    }

    /// <summary>
    /// Resolves the <see cref="HttpClient"/> for a server's transport and applies any
    /// static auth headers to <paramref name="options"/>. Throws when auth is configured
    /// but incomplete — a half-configured server must fail loudly rather than connect
    /// with no credential.
    /// </summary>
    private HttpClient ResolveTransportHttpClient(
        string serverName,
        McpServerAuthConfig? auth,
        HttpClientTransportOptions options)
    {
        if (auth is not { IsConfigured: true })
            return _httpClient;

        if (!auth.IsValid)
            throw new McpConnectionException(
                $"MCP server '{serverName}' has auth type {auth.Type} configured but its credential " +
                "settings are incomplete. Provide the required fields for that auth type, or set the " +
                "auth type to None.");

        switch (auth.Type)
        {
            case McpServerAuthType.ApiKey:
                options.AdditionalHeaders = new Dictionary<string, string> { [auth.ApiKeyHeader] = auth.ApiKey! };
                return _httpClient;

            case McpServerAuthType.Bearer:
                options.AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {auth.BearerToken}" };
                return _httpClient;

            case McpServerAuthType.Entra:
                // Per-server client: EntraTokenAuthHandler mints a fresh, auto-rotating
                // token per request and forwards to the shared SSRF-guard handler.
                // disposeHandler:false (set in DisposeAsync's cleanup) keeps the shared
                // terminal handler alive. Creation is serialized per server by the caller's
                // connection lock, so GetOrAdd's factory runs at most once per server.
                return _entraClients.GetOrAdd(
                    serverName,
                    _ => new HttpClient(EntraTokenAuthHandler.Create(auth, _antiSsrfHandler), disposeHandler: false));

            default:
                throw new McpConnectionException(
                    $"MCP server '{serverName}' uses unsupported auth type '{auth.Type}'.");
        }
    }

    private static readonly HashSet<string> BlockedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "169.254.169.254",
        "metadata.google.internal",
        "metadata.goog"
    };

    // Cheap pre-flight check: reject non-http(s) schemes early and fail fast on
    // well-known metadata hostnames. Comprehensive IP-range filtering (RFC 1918,
    // loopback, link-local, IMDS, IPv6 ULA) and DNS-rebinding defense are handled
    // at connect time by the AntiSSRF handler backing this manager's shared HTTP client.
    private static void ValidateMcpServerUrl(Uri uri, string serverName)
    {
        if (uri.Scheme is not ("http" or "https"))
            throw new McpConnectionException(
                $"MCP server '{serverName}' uses unsupported scheme '{uri.Scheme}'. Only http/https are allowed.");

        if (BlockedHosts.Contains(uri.Host))
            throw new McpConnectionException(
                $"MCP server '{serverName}' targets a cloud metadata endpoint ({uri.Host}), which is blocked.");
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

        // Per-server Entra clients were built with disposeHandler:false, so disposing them
        // releases the client wrapper without touching the shared AntiSSRF handler their
        // token handlers wrap. The token handlers hold only a managed TokenCredential and
        // are reclaimed by the GC.
        foreach (var kvp in _entraClients)
        {
            kvp.Value.Dispose();
        }

        _entraClients.Clear();

        foreach (var kvp in _connectionLocks)
        {
            kvp.Value.Dispose();
        }

        _connectionLocks.Clear();

        // disposeHandler:false at construction — disposes the client wrapper only,
        // leaving the shared AntiSSRF handler intact for the factory to own.
        _httpClient.Dispose();
    }
}
