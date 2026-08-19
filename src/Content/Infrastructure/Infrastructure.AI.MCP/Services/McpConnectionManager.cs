using System.Collections.Concurrent;
using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Bundles;
using Application.AI.Common.Interfaces.Egress;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Services.Sandbox;
using Domain.AI.Sandbox;
using Domain.Common;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.BundleExecution;
using Domain.Common.Config.AI.MCP;
using Domain.Common.Helpers;
using Infrastructure.AI.Egress;
using Infrastructure.AI.Identity;
using Infrastructure.AI.MCP.Egress;
using Infrastructure.AI.Skills;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    private readonly IBundleOwnedMcpServerRegistry _bundleOwnedServers;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAmbientRequestScope _ambientScope;
    private readonly IServiceProvider _rootServices;
    private readonly IEgressAuditWriter _egressAuditWriter;
    private readonly ILogger<EgressPolicyDelegatingHandler> _egressHandlerLogger;
    private readonly TimeProvider _timeProvider;
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly IGovernanceAuditService? _governanceAuditService;
    private readonly IOptionsMonitor<GovernanceConfig>? _governanceConfig;

    // A single flat cache keyed by bare serverName, shared across BOTH _config and _bundleOwnedServers —
    // safe today because the two namespacing schemes never collide (host names are plain or
    // {pluginName}:{name}; bundle names are {bundleId GUID}:{name}). A future change that restructures
    // these into per-source caches should preserve that invariant, not merely mirror the field shapes.
    private readonly ConcurrentDictionary<string, McpClient> _clients = new();
    private readonly ConcurrentDictionary<string, HttpClient> _entraClients = new();

    /// <summary>
    /// Per-bundle-server clients whose handler chain is <see cref="BundleMcpEgressAttributionHandler"/> →
    /// a dedicated <see cref="EgressPolicyDelegatingHandler"/> → the shared <see cref="_antiSsrfHandler"/>
    /// — see <see cref="ResolveBundleEgressClient"/>. Kept separate from <see cref="_entraClients"/> even
    /// though both are per-server client caches: an Entra client wraps only a lightweight token handler
    /// around the SAME shared terminal handler, while a bundle-egress client owns two additional handler
    /// instances of its own (still never the shared AntiSSRF handler itself — see the disposal remarks on
    /// <see cref="DisposeAsync"/>).
    /// </summary>
    private readonly ConcurrentDictionary<string, HttpClient> _bundleEgressClients = new();

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _connectionLocks = new();
    private bool _disposed;

    /// <summary>
    /// Live bundle-owned sandboxed stdio sessions across the whole host, bounded by
    /// <see cref="BundleStdioMcpServersConfig.MaxConcurrentSessions"/> — see the security-review
    /// finding recorded on that property. Incremented before <see cref="ISandboxSessionFactory.StartSessionAsync"/>
    /// is ever called; decremented either immediately on a failed start or via
    /// <see cref="ScopedSandboxSession"/>'s disposal callback once a session actually ends.
    /// </summary>
    private int _liveSandboxedStdioSessions;

    /// <summary>
    /// Initializes a new instance of the <see cref="McpConnectionManager"/> class.
    /// </summary>
    /// <remarks>
    /// The SSRF defense is a hard dependency, not a configuration option: every HTTP/SSE transport this
    /// manager builds — host-configured or bundle-owned — is built on the <c>AntiSSRFHandler</c> produced
    /// by <paramref name="antiSsrfHandlerFactory"/>, which performs connect-time IP filtering (RFC 1918,
    /// loopback, link-local, IMDS, IPv6 ULA) and redirect re-validation. There is no code path that
    /// constructs an unguarded client, so SSRF protection cannot be silently omitted by misconfiguration.
    /// <para>
    /// A host-configured server's shared client applies only the AntiSSRF ring, not the outer
    /// <c>EgressPolicyDelegatingHandler</c> (hostname allowlist + JSONL audit) the general egress
    /// <see cref="HttpClient"/> composes — those servers are explicitly admin-configured, and connections
    /// are established outside an agent turn (e.g. startup tool discovery) where that handler's required
    /// agent identity would be absent. A <strong>bundle-owned</strong> server is different: it is
    /// untrusted, uploader-declared input, so its connections go through a dedicated, per-server chain
    /// (<see cref="ResolveBundleEgressClient"/>) that applies BOTH rings on every request, attributing
    /// each one to the owning bundle via <see cref="BundleMcpEgressAttributionHandler"/> rather than
    /// depending on whatever ambient identity the calling code happens to have.
    /// </para>
    /// </remarks>
    public McpConnectionManager(
        ILogger<McpConnectionManager> logger,
        ILoggerFactory loggerFactory,
        AntiSsrfHandlerFactory antiSsrfHandlerFactory,
        McpServersConfig config,
        IBundleOwnedMcpServerRegistry bundleOwnedServers,
        IServiceScopeFactory scopeFactory,
        IAmbientRequestScope ambientScope,
        IServiceProvider rootServices)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        // The shared SSRF-guard terminal handler. Captured so Entra connections and bundle-egress
        // connections can each wrap it with their own outer handler(s) while still routing through the
        // same connect-time IP filter.
        _antiSsrfHandler = antiSsrfHandlerFactory.GetOrCreate();
        // disposeHandler: false — the AntiSSRF handler is a shared singleton owned by
        // the factory; this client must not dispose it.
        _httpClient = new HttpClient(_antiSsrfHandler, disposeHandler: false);
        _config = config;
        _bundleOwnedServers = bundleOwnedServers;
        _scopeFactory = scopeFactory;
        _ambientScope = ambientScope;
        _rootServices = rootServices;
        // Resolved ONCE here — all three are singleton registrations, so the container returns the SAME
        // cached instance on every call and this costs nothing beyond construction. This is deliberately
        // NOT done by resolving EgressPolicyDelegatingHandler itself from rootServices per bundle-owned
        // server in ResolveBundleEgressClient: that type is registered transient, and a transient
        // IDisposable resolved directly from the ROOT container (not a scope) is tracked by the container
        // and never released until process shutdown — the container leaks one handler per distinct
        // bundle-owned server name for the remainder of the host's lifetime. Resolving these three
        // singleton dependencies here and constructing EgressPolicyDelegatingHandler by hand keeps this
        // manager the sole owner of every instance it builds, exactly like the pre-existing Entra client
        // cache below.
        _egressAuditWriter = rootServices.GetRequiredService<IEgressAuditWriter>();
        _egressHandlerLogger = rootServices.GetRequiredService<ILogger<EgressPolicyDelegatingHandler>>();
        _timeProvider = rootServices.GetService<TimeProvider>() ?? TimeProvider.System;
        _appConfig = rootServices.GetRequiredService<IOptionsMonitor<AppConfig>>();
        // Optional, like ToolPermissionProfileResolver's identical convention (#419): a composition
        // root that never calls AddGovernance still constructs this manager; the sandboxed-session
        // refusal branches below (#431) just get no durable audit trail for this path.
        _governanceAuditService = rootServices.GetService<IGovernanceAuditService>();
        _governanceConfig = rootServices.GetService<IOptionsMonitor<GovernanceConfig>>();
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

        using var _ = await AcquireConnectionLockAsync(serverName, cancellationToken);

        if (_clients.TryGetValue(serverName, out existing))
            return existing;

        return await CreateAndCacheClientAsync(serverName, cancellationToken);
    }

    /// <summary>
    /// Checks if a server connection is active and healthy.
    /// </summary>
    public bool IsConnected(string serverName)
    {
        return _clients.ContainsKey(serverName);
    }

    /// <summary>
    /// Bound on how long <see cref="DisconnectAsync"/> waits for the per-server connection lock before
    /// proceeding without it (#378). This method's only production caller is bundle teardown
    /// (<c>BundleMcpServerRegistrar</c>), which has no cancellation token of its own to bound a wait
    /// with — a fixed timeout is the only option that keeps teardown bounded regardless of caller. Long
    /// enough that the realistic race (a fast in-flight connect finishing just before or during a
    /// disconnect for the same server) resolves cleanly under the lock; short enough that a genuinely
    /// hung connect attempt (a stdio child process that never exits, a remote that never completes its
    /// handshake) cannot block teardown for more than this.
    /// </summary>
    private static readonly TimeSpan DisconnectLockTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Disconnects from a specific server and removes the cached connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Takes the SAME per-server connection lock <see cref="GetClientAsync"/> and
    /// <see cref="ReconnectAsync"/> use, through the SAME <see cref="AcquireConnectionLockAsync"/> — not a
    /// second lock-acquisition method — bounded by a locally-synthesized <see cref="CancellationTokenSource"/>
    /// (#378): unlike those two callers, this method's only production caller (bundle teardown) has no
    /// cancellation token of its own to bound an unconditional wait with, so a hung connect attempt for the
    /// same server (mid-handshake, inside <see cref="CreateClientAsync"/>) could otherwise block teardown
    /// indefinitely — worse than the race the lock closes. When the token fires before the lock is free,
    /// eviction proceeds anyway, unlocked, exactly as this method always has: <c>ConcurrentDictionary.TryRemove</c>
    /// is already atomic, so concurrent callers of this method cannot corrupt <see cref="_clients"/> between
    /// themselves either way. What the lock actually buys, when acquired, is exclusion against a
    /// concurrent <see cref="CreateAndCacheClientAsync"/> caching a NEW client for this server between
    /// this method's remove and its return — the pre-existing gap this method's doc used to describe as
    /// unclosed is now closed for every realistic (non-hung) case.
    /// </para>
    /// <para>
    /// Still does not protect a caller that already holds a reference to the client this disposes,
    /// obtained via <see cref="GetClientAsync"/>'s unlocked fast-path read before this method ran — that
    /// caller can still observe <see cref="ObjectDisposedException"/> mid-use. Closing that race needs
    /// reference-counted or generation-tagged client leases, a larger change to this type's
    /// client-lifetime model; tracked as a known limitation, not solved here.
    /// </para>
    /// </remarks>
    public async Task DisconnectAsync(string serverName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var timeoutCts = new CancellationTokenSource(DisconnectLockTimeout);
        IDisposable? lockScope = null;
        try
        {
            lockScope = await AcquireConnectionLockAsync(serverName, timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Disconnect from MCP server '{ServerName}' proceeded without the connection lock after " +
                "waiting {Timeout} — a connect attempt for this server was still in flight.",
                serverName, DisconnectLockTimeout);
        }

        try
        {
            await EvictAsync(serverName);
        }
        finally
        {
            lockScope?.Dispose();
        }
    }

    /// <summary>
    /// Evicts <paramref name="failedClient"/> and connects fresh, but only if it is still the cached
    /// client for <paramref name="serverName"/>.
    /// </summary>
    /// <remarks>
    /// A cached client that a previous call already used can go stale — the remote restarted or evicted
    /// the session — and using it fails with no warning beyond the failed call itself (#385). The caller
    /// that observed that failure calls this to recover. Without the reference check, two callers that
    /// both saw the SAME stale client and both raced here would each run their own evict-then-reconnect:
    /// the second would tear down the fresh connection the first just paid for and connect a third time.
    /// The check collapses that to one reconnect — whichever caller acquires the per-server lock first
    /// evicts and reconnects; every later caller finds the cache already holding a client that isn't the
    /// one it saw fail, and simply returns that instead.
    /// </remarks>
    /// <param name="serverName">The server name from configuration.</param>
    /// <param name="failedClient">The client instance the caller observed a failure on.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="McpConnectionException">Thrown when the fresh connection attempt fails.</exception>
    public async Task<McpClient> ReconnectAsync(string serverName, McpClient failedClient, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var _ = await AcquireConnectionLockAsync(serverName, cancellationToken);

        if (_clients.TryGetValue(serverName, out var current) && !ReferenceEquals(current, failedClient))
            return current;

        var stale = DetachClient(serverName);

        // Disposing the stale client and connecting fresh are independent once detach has happened — run
        // them concurrently rather than paying dispose-then-connect serially. For a hung/misbehaving
        // remote (exactly the case that got here), tearing down the old session — a stdio child-process
        // exit or an HTTP/SSE session close against a remote that just rejected it — can be the slower
        // half; every other caller of GetClientAsync for this server is already queued behind this same
        // lock, so serializing it in front of the new connect only lengthens their wait for no benefit.
        var disposeStale = stale is not null ? DisposeStaleClientAsync(stale, serverName) : Task.CompletedTask;
        var connect = CreateAndCacheClientAsync(serverName, cancellationToken);
        await Task.WhenAll(disposeStale, connect);

        return await connect;
    }

    /// <summary>
    /// Connects to <paramref name="serverName"/> and caches the result. Caller must hold that server's
    /// connection lock.
    /// </summary>
    private async Task<McpClient> CreateAndCacheClientAsync(string serverName, CancellationToken cancellationToken)
    {
        var client = await CreateClientAsync(serverName, cancellationToken);
        _clients[serverName] = client;
        return client;
    }

    /// <summary>
    /// Removes and disposes the cached client and its associated per-server HTTP clients for
    /// <paramref name="serverName"/>. Safe to call with or without the connection lock held — every
    /// mutation here is an atomic <c>ConcurrentDictionary.TryRemove</c> call.
    /// </summary>
    private async Task EvictAsync(string serverName)
    {
        var client = DetachClient(serverName);
        if (client is not null)
        {
            await client.DisposeAsync();
            _logger.LogInformation("Disconnected from MCP server '{ServerName}'", serverName);
        }
    }

    /// <summary>
    /// Removes the cached <see cref="McpClient"/> for <paramref name="serverName"/> from <see cref="_clients"/>
    /// and, in the same call, releases its associated per-server Entra and bundle-egress HTTP clients (and
    /// their pooled sockets). Both HTTP caches were built with <c>disposeHandler:false</c>, so this leaves
    /// the shared AntiSSRF handler intact; a later reconnect recreates a fresh token-injecting or
    /// attribution+egress-policy handler pair as needed. Does NOT dispose the returned <see cref="McpClient"/>
    /// — the caller decides whether to await that or run it concurrently with other work.
    /// </summary>
    private McpClient? DetachClient(string serverName)
    {
        _clients.TryRemove(serverName, out var client);
        TryDisposeCachedClient(_entraClients, serverName);
        TryDisposeCachedClient(_bundleEgressClients, serverName);
        return client;
    }

    /// <summary>
    /// Disposes a client already detached from <see cref="_clients"/>. Swallows and logs its own
    /// disposal failures rather than propagating them: this disposes something already being discarded
    /// (a stale or superseded connection), so a failure to tear it down cleanly must not fail the
    /// reconnect that is replacing it.
    /// </summary>
    private async Task DisposeStaleClientAsync(McpClient client, string serverName)
    {
        try
        {
            await client.DisposeAsync();
            _logger.LogInformation("Disconnected from MCP server '{ServerName}'", serverName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cleanly dispose the stale MCP client for '{ServerName}'", serverName);
        }
    }

    private readonly struct ConnectionLockScope(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }

    private async Task<ConnectionLockScope> AcquireConnectionLockAsync(string serverName, CancellationToken cancellationToken)
    {
        var connectionLock = GetOrCreateConnectionLock(serverName);
        await connectionLock.WaitAsync(cancellationToken);
        return new ConnectionLockScope(connectionLock);
    }

    private SemaphoreSlim GetOrCreateConnectionLock(string serverName) =>
        _connectionLocks.GetOrAdd(serverName, _ => new SemaphoreSlim(1, 1));

    private static void TryDisposeCachedClient(ConcurrentDictionary<string, HttpClient> cache, string serverName)
    {
        if (cache.TryRemove(serverName, out var client))
        {
            client.Dispose();
        }
    }

    private static void DisposeCachedClients(ConcurrentDictionary<string, HttpClient> cache)
    {
        foreach (var kvp in cache)
        {
            kvp.Value.Dispose();
        }

        cache.Clear();
    }

    /// <summary>
    /// Gets the names of all configured and enabled servers.
    /// </summary>
    /// <remarks>
    /// <strong>Deliberately host-only.</strong> This enumerates <em>only</em> <see cref="_config"/> —
    /// never <see cref="_bundleOwnedServers"/>. Do not add it here; see
    /// <see cref="IBundleOwnedMcpServerRegistry"/>'s own doc comment for why a bundle-owned server must
    /// never be reachable from this method.
    /// </remarks>
    public IEnumerable<string> GetConfiguredServerNames()
    {
        return _config.Servers
            .Where(kvp => kvp.Value.Enabled)
            .Select(kvp => kvp.Key);
    }

    private async Task<McpClient> CreateClientAsync(string serverName, CancellationToken cancellationToken)
    {
        // Host dictionary first (trusted source wins outright), then the bundle-owned registry as a
        // fallback for an exact-name lookup only — never enumerated, only resolved by name. This is how
        // a bundle run's own already-envelope-gated resolution (ToolChainBuilder.ProvisionToolAsync,
        // ResolveInjectedMcpToolsAsync's envelope-armed branch, BundleRunExecutor.DiscoverToolNamesAsync)
        // reaches its own server; without it, a bundle run could never use its own registered tools.
        // Whichever branch resolves the definition also tells us which egress treatment it gets below.
        var isBundleOwned = false;
        if (!_config.Servers.TryGetValue(serverName, out var definition))
        {
            if (!_bundleOwnedServers.TryGetValue(serverName, out definition))
                throw new McpConnectionException($"MCP server '{serverName}' is not configured.");
            isBundleOwned = true;
        }

        if (!definition.Enabled)
            throw new McpConnectionException($"MCP server '{serverName}' is disabled.");

        _logger.LogInformation(
            "Connecting to MCP server '{ServerName}' via {Transport}...",
            serverName, definition.Type);

        try
        {
            var transport = CreateTransport(serverName, definition, isBundleOwned);

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

    private IClientTransport CreateTransport(string serverName, McpServerDefinition definition, bool isBundleOwned)
    {
        return definition.Type switch
        {
            // A bundle-owned stdio server runs inside the sandbox, never directly on the host —
            // see #371 and BundleStagingService.LogStdioRejected for the RCE rationale. Reachable
            // only when an operator has both opted into AppConfig.AI.BundleExecution.StdioMcpServers
            // and configured a container image; BundleStagingService.TryBuildAndRegisterOneServer
            // is the gate that decides whether isBundleOwned is ever true here for a stdio server.
            McpServerType.Stdio when isBundleOwned => new SandboxedStdioClientTransport(
                serverName,
                ct => StartSandboxedStdioSessionAsync(serverName, definition, ct),
                _loggerFactory),
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
            McpServerType.Http or McpServerType.Sse => CreateHttpTransport(serverName, definition, isBundleOwned),
            _ => throw new McpConnectionException($"Unsupported MCP transport type: {definition.Type}")
        };
    }

    /// <summary>
    /// Starts the sandboxed session backing a bundle-owned stdio MCP server. Resolves
    /// <see cref="ISandboxSessionFactory"/> from a fresh scope — it is scoped (it depends on
    /// <c>ISandboxEgressPreflight</c>, which resolves the ambient agent identity per call) while
    /// this manager is a process-lifetime singleton — and wraps the result in
    /// <see cref="ScopedSandboxSession"/> so the scope survives exactly as long as the session
    /// does, past the end of this method. See that type's own remarks for why that makes this a
    /// different shape from <c>TerraformGenerator</c>'s per-run <c>ISandboxExecutor</c>
    /// resolution, not the same one.
    /// </summary>
    /// <remarks>
    /// Container isolation only: the harness has no per-bundle Process-tier allowlist concept, and
    /// <c>ProcessSandboxSessionFactory</c> refuses a request that carries
    /// <see cref="SandboxSessionRequest.WorkspaceSeedDirectory"/> outright, so this path could never
    /// downgrade to that tier even by misconfiguration. Resource limits use sandbox defaults.
    /// <para>
    /// The container image comes from <c>AppConfig.AI.BundleExecution.StdioMcpServers.ContainerImage</c>
    /// — an operator-set default shared by every bundle-owned stdio server on this host, not a
    /// per-bundle choice: a bundle-owned name contains a fresh GUID per staging, so the sandbox's
    /// existing per-tool image override lookup can never match one. The workspace is seeded from
    /// <see cref="McpServerDefinition.SandboxSeedDirectory"/> — the bundle's own staged directory,
    /// set only by <c>BundleStagingService</c> at registration — so the server's own files are
    /// present when its process starts.
    /// </para>
    /// <para>
    /// The permission profile is resolved through <see cref="ToolPermissionProfileResolver"/> —
    /// not built as an inline literal — so this path consults the same override registry
    /// (<c>SandboxConfig.ToolOverrides</c>) that <c>SandboxExecutionOptions.ToolOverrides</c>
    /// (read by <c>DockerContainerLaunchPreparer.ResolveImage</c> for the container image) is a
    /// sibling of, instead of a second, unconsulted one. Precisely what this achieves today,
    /// verified rather than assumed:
    /// <list type="bullet">
    /// <item><description>Only <see cref="ToolPermissionProfile.EffectiveCapabilities"/> and
    /// <see cref="ToolPermissionProfile.MinimumIsolation"/> reach the container launch (via
    /// <c>DockerContainerLaunchPreparer.BuildContainerParams</c>) — the path/host allow/deny
    /// config that once lived on this profile was removed as dead code (#405): it was read from
    /// config but nothing on the live tool-call path ever checked a requested path or host against
    /// it.</description></item>
    /// <item><description>A bundle-owned name is outside the bounded first-party key set, so the
    /// resolver's base declaration is <see cref="ToolCapability.None"/>/<see cref="SandboxIsolationLevel.None"/>
    /// — <c>EffectiveCapabilities</c> is <c>RequiredCapabilities &amp; ~DeniedCapabilities</c>, and
    /// ANDing anything against a <c>None</c> requirement is still <c>None</c>, so a
    /// <c>DeniedCapabilities</c> override has no observable effect here regardless of what an
    /// operator writes. The isolation floor is then raised to
    /// <see cref="SandboxIsolationLevel.Container"/> unconditionally via <c>WithMinimumIsolationAtLeast</c>
    /// (#433), so a <c>MinimumIsolation</c> override can never lower it either.</description></item>
    /// <item><description>A bundle-owned server name is <c>{bundleId}:{serverName}</c> —
    /// <c>bundleId</c> is a fresh GUID per staging (<c>BundleStagingService</c>) — so no operator
    /// can currently author a <c>ToolOverrides</c> key that matches one at all. The resolver call
    /// is inert-by-construction today, not merely unexercised.</description></item>
    /// </list>
    /// The wiring is kept anyway: it is the correct long-term shape (one override registry
    /// consulted uniformly by tool name, not two, one of them silently skipped), and the resolved
    /// <see cref="ToolCapability.None"/> is itself the reason a bundle-owned container gets no
    /// network access and a read-only workspace bind mount by default — a stronger posture than
    /// leaving the override path unconsulted would suggest.
    /// </para>
    /// </remarks>
    private async Task<Result<ISandboxSession>> StartSandboxedStdioSessionAsync(
        string serverName, McpServerDefinition definition, CancellationToken cancellationToken)
    {
        // Claimed before anything else — cheapest possible check, and it must bracket every failure
        // path below (a resolver throwing, the containment check rejecting, the factory itself
        // failing), not just the success path, or a slot leaks. ownershipTransferred below (already
        // tracking whether the DI scope's lifetime transferred to the returned session) transfers
        // this slot's ownership at the exact same point, so one flag does double duty rather than
        // needing a second.
        var maxConcurrentSessions = _appConfig.CurrentValue.AI.BundleExecution.StdioMcpServers.MaxConcurrentSessions;
        if (Interlocked.Increment(ref _liveSandboxedStdioSessions) > maxConcurrentSessions)
        {
            Interlocked.Decrement(ref _liveSandboxedStdioSessions);
            var capReason = $"Host-wide bundle stdio sandbox session cap ({maxConcurrentSessions}) reached; refusing to start another.";
            _governanceAuditService.LogIfAuditEnabled(_governanceConfig, "system", serverName, $"host_session_cap_exceeded:{maxConcurrentSessions}");
            return Result<ISandboxSession>.Fail(capReason);
        }

        // Nullable, and assigned only once CreateAsyncScope() itself succeeds — that call is not
        // guaranteed exception-free (e.g. an already-disposed root provider during host shutdown),
        // and it sat outside this method's try/finally in an earlier draft, so a throw there
        // leaked the slot claimed above for the rest of the process's life. Declaring it here and
        // assigning inside the try means the finally below can tell "never created" (nothing to
        // dispose) apart from "created but not yet transferred" (must dispose) via a single null
        // check, without a second try/finally layer.
        AsyncServiceScope? scope = null;
        var ownershipTransferred = false;
        try
        {
            scope = _scopeFactory.CreateAsyncScope();
            var sessionFactory = scope.Value.ServiceProvider
                .GetRequiredKeyedService<ISandboxSessionFactory>(SandboxIsolationLevel.Container);
            var profileResolver = scope.Value.ServiceProvider.GetRequiredService<ToolPermissionProfileResolver>();

            var resolvedProfile = profileResolver.Resolve(serverName);
            var permissionProfile = resolvedProfile.WithMinimumIsolationAtLeast(SandboxIsolationLevel.Container);

            if (definition.SandboxSeedDirectory is { } seedDirectory && !IsWithinConfiguredStagingRoot(seedDirectory))
            {
                // A structural containment check, not just a convention: today's ONLY writer of
                // SandboxSeedDirectory (BundleStagingService) only ever sets it to the bundle's own
                // staged root, so this never fires in production. It exists so a future caller of
                // this field cannot turn "seed a sandbox workspace" into "copy an arbitrary host
                // directory into an untrusted, bundle-launched container" without this check also
                // having to be deliberately bypassed, not merely never written in the first place.
                var containmentReason = "Sandbox workspace seed directory is outside the configured bundle staging root — refusing to seed.";
                _governanceAuditService.LogIfAuditEnabled(_governanceConfig, "system", serverName, "seed_outside_staging_root");
                return Result<ISandboxSession>.Fail(containmentReason);
            }

            var request = new SandboxSessionRequest
            {
                ToolName = serverName,
                Limits = new ResourceLimits(),
                PermissionProfile = permissionProfile,
                Command = definition.Command,
                ArgumentList = definition.Args,
                EnvironmentVariables = definition.Env.Count > 0 ? definition.Env : null,
                ContainerImage = _appConfig.CurrentValue.AI.BundleExecution.StdioMcpServers.ContainerImage is { Length: > 0 } image
                    ? image
                    : null,
                WorkspaceSeedDirectory = definition.SandboxSeedDirectory
            };

            var result = await sessionFactory.StartSessionAsync(request, cancellationToken);
            if (!result.IsSuccess)
            {
                _governanceAuditService.LogIfAuditEnabled(_governanceConfig?.CurrentValue, "system", serverName,
                    () => $"session_factory_failed:{(result.Errors.Count > 0 ? string.Join("; ", result.Errors) : "unknown error")}");
                return result;
            }

            // From here on, ownership of both the scope AND this session's claimed concurrency slot
            // transfers to the returned session — two composed decorators, each responsible for
            // releasing exactly one of them on DisposeAsync, not one type juggling both.
            ownershipTransferred = true;
            var scopedSession = new ScopedSandboxSession(result.Value!, scope.Value);
            return Result<ISandboxSession>.Success(new SlotReleasingSandboxSession(
                scopedSession, releaseSlot: () => Interlocked.Decrement(ref _liveSandboxedStdioSessions)));
        }
        finally
        {
            if (!ownershipTransferred)
            {
                Interlocked.Decrement(ref _liveSandboxedStdioSessions);
                if (scope is { } createdScope)
                    await createdScope.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="seedDirectory"/> resolves under the SAME bundle staging root
    /// <see cref="SkillContentRoots.BundleStaging"/> resolves — the only tree a sandbox workspace may
    /// ever be seeded from. Deliberately re-derives that resolution here rather than trusting the
    /// caller: see the containment-check remarks at this method's one call site.
    /// </summary>
    /// <remarks>
    /// <see cref="SkillContentRoots.BundleStaging"/> already contains the "configured <c>TempRoot</c>,
    /// or the system-temp fallback" branch internally — the ONE place that decision is made, per that
    /// class's own doc comment — so this method must not (and no longer does) re-derive that branch a
    /// second time itself; a caller re-deriving it is exactly the drift risk the class's own doc warns
    /// about. A single containment check against one target uses <see cref="PathScope.IsSameOrUnder"/>,
    /// which normalizes both sides internally, rather than the <c>*Normalized</c> overload meant for
    /// comparing many targets against one already-normalized base.
    /// </remarks>
    private bool IsWithinConfiguredStagingRoot(string seedDirectory) =>
        PathScope.IsSameOrUnder(seedDirectory, SkillContentRoots.BundleStaging(_appConfig.CurrentValue));

    private HttpClientTransport CreateHttpTransport(string serverName, McpServerDefinition definition, bool isBundleOwned)
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
        var httpClient = ResolveTransportHttpClient(serverName, definition.Auth, options, isBundleOwned);

        return new HttpClientTransport(options, httpClient, _loggerFactory);
    }

    /// <summary>
    /// Thin dispatcher to the host-configured or bundle-owned resolution path — the two share no logic
    /// (different credential model, different handler chain), so each gets its own method rather than
    /// one method with two unrelated bodies stitched together by an <c>if</c>.
    /// </summary>
    private HttpClient ResolveTransportHttpClient(
        string serverName,
        McpServerAuthConfig? auth,
        HttpClientTransportOptions options,
        bool isBundleOwned)
    {
        return isBundleOwned
            ? ResolveBundleOwnedTransportHttpClient(serverName, auth)
            : ResolveHostConfiguredTransportHttpClient(serverName, auth, options);
    }

    /// <summary>
    /// Resolves the client for a bundle-owned server. Bundle manifests can never populate <c>Auth</c>
    /// (see the guard below), so there are no static headers to apply — the credential-free,
    /// egress-attributed handler chain lives entirely in <see cref="ResolveBundleEgressClient"/>.
    /// </summary>
    private HttpClient ResolveBundleOwnedTransportHttpClient(string serverName, McpServerAuthConfig? auth)
    {
        // A bundle's own MCP server definition never carries Auth — McpServerDefinitionBuilder does
        // not populate it for bundle-declared servers, so a bundle cannot induce the host to attach
        // a credential to its own endpoint. Fail loudly rather than silently skip the egress-attributed
        // path below if that invariant is ever violated by a future change.
        if (auth is { IsConfigured: true })
            throw new McpConnectionException(
                $"Bundle-owned MCP server '{serverName}' has auth configured, which bundle-declared " +
                "servers do not support.");

        return ResolveBundleEgressClient(serverName);
    }

    /// <summary>
    /// Resolves the client for a host-configured (admin-declared) server and applies any static auth
    /// headers to <paramref name="options"/>. Throws when auth is configured but incomplete — a
    /// half-configured server must fail loudly rather than connect with no credential.
    /// </summary>
    private HttpClient ResolveHostConfiguredTransportHttpClient(
        string serverName, McpServerAuthConfig? auth, HttpClientTransportOptions options)
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

    /// <summary>
    /// Builds (or returns the cached) egress-attributed client for one bundle-owned server. The handler
    /// chain is <see cref="BundleMcpEgressAttributionHandler"/> (outer — stamps every request with a
    /// synthetic identity scoped to this exact server) → a dedicated <see cref="EgressPolicyDelegatingHandler"/>
    /// built from the SAME singleton dependencies (<see cref="_egressAuditWriter"/>, <see cref="_egressHandlerLogger"/>,
    /// <see cref="_timeProvider"/>) production's own "egress" named <see cref="HttpClient"/> registration
    /// (<c>Infrastructure.AI/DependencyInjection.Egress.cs</c>) resolves its handler from — wrapping
    /// <see cref="_antiSsrfHandler"/> (inner/terminal, the SAME shared singleton every other path uses —
    /// SSRF protection is never weakened or duplicated).
    /// <c>disposeHandler</c> is deliberately <see langword="false"/>, mirroring the Entra client cache:
    /// <see cref="DelegatingHandler"/>'s own disposal cascades into its
    /// <see cref="DelegatingHandler.InnerHandler"/>, so <see langword="true"/> here would eventually
    /// dispose the SHARED AntiSSRF handler out from under every other connection this manager owns.
    /// <c>EgressPolicyDelegatingHandler</c> is built with <see langword="new"/> rather than resolved from
    /// <see cref="_rootServices"/>, deliberately: that type is registered transient, and a transient
    /// <see cref="IDisposable"/> resolved directly from the ROOT container is tracked and never released
    /// by the container until process shutdown — one handler would leak per distinct bundle-owned server
    /// name for the life of the host. Manually constructing it keeps this manager the sole owner of every
    /// instance it builds, so the two handler instances this method allocates hold no unmanaged resources
    /// and are safe to leave to the GC once the cache entry is dropped, exactly as before.
    /// </summary>
    private HttpClient ResolveBundleEgressClient(string serverName)
    {
        return _bundleEgressClients.GetOrAdd(serverName, name =>
        {
            var egressHandler = new EgressPolicyDelegatingHandler(
                _rootServices, _ambientScope, _egressAuditWriter, _egressHandlerLogger, _timeProvider)
            {
                InnerHandler = _antiSsrfHandler
            };
            var attributionHandler = new BundleMcpEgressAttributionHandler(name, _scopeFactory, _ambientScope)
            {
                InnerHandler = egressHandler
            };
            return new HttpClient(attributionHandler, disposeHandler: false);
        });
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

        // Both per-server client caches were built with disposeHandler:false, so disposing each client
        // releases the wrapper only, without touching the shared AntiSSRF handler its own handler(s)
        // wrap — the credential/attribution handlers each one owns hold no unmanaged resources and are
        // reclaimed by the GC.
        DisposeCachedClients(_entraClients);
        DisposeCachedClients(_bundleEgressClients);

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
