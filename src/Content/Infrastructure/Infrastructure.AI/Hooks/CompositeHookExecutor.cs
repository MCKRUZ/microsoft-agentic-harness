using System.Diagnostics;
using System.Text.Json;
using Application.AI.Common.Interfaces.Hooks;
using Domain.AI.Hooks;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config.AI.Hooks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Hooks;

/// <summary>
/// Executes matching hooks in parallel with per-hook timeouts, error isolation,
/// and concurrency throttling via <see cref="SemaphoreSlim"/>.
/// Dispatches to type-specific execution based on <see cref="Domain.AI.Hooks.HookType"/>.
/// </summary>
public sealed class CompositeHookExecutor : IHookExecutor
{
    private readonly IHookRegistry _registry;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<HooksConfig> _config;
    private readonly ILogger<CompositeHookExecutor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CompositeHookExecutor"/> class.
    /// </summary>
    /// <param name="registry">The hook registry to query for matching hooks.</param>
    /// <param name="httpClientFactory">Factory for creating HTTP clients for webhook hooks.</param>
    /// <param name="config">Hook system configuration with concurrency and timeout settings.</param>
    /// <param name="logger">Logger for execution diagnostics.</param>
    public CompositeHookExecutor(
        IHookRegistry registry,
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<HooksConfig> config,
        ILogger<CompositeHookExecutor> logger)
    {
        _registry = registry;
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HookResult>> ExecuteHooksAsync(
        HookEvent hookEvent,
        HookExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var config = _config.CurrentValue;

        if (!config.Enabled)
            return [];

        var hooks = _registry.GetHooksForEvent(hookEvent, context.ToolName);

        if (hooks.Count == 0)
            return [];

        using var semaphore = new SemaphoreSlim(config.MaxParallelHooks);
        var tasks = new Task<HookResult>[hooks.Count];

        for (var i = 0; i < hooks.Count; i++)
        {
            var hook = hooks[i];
            tasks[i] = ExecuteWithThrottleAsync(semaphore, hook, context, cancellationToken);
        }

        var results = await Task.WhenAll(tasks);

        UnregisterRunOnceHooks(hooks);

        return results;
    }

    private async Task<HookResult> ExecuteWithThrottleAsync(
        SemaphoreSlim semaphore,
        HookDefinition hook,
        HookExecutionContext context,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            return await ExecuteSingleHookAsync(hook, context, cancellationToken);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<HookResult> ExecuteSingleHookAsync(
        HookDefinition hook,
        HookExecutionContext context,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(hook.TimeoutMs);

        try
        {
            var result = hook.Type switch
            {
                HookType.Prompt => ExecutePromptHook(hook, context),
                HookType.Http => await ExecuteHttpHookAsync(hook, context, timeoutCts.Token),
                HookType.Command => ExecuteCommandHook(hook),
                HookType.Middleware => ExecuteMiddlewareHook(hook),
                _ => HookResult.PassThrough()
            };

            sw.Stop();

            _logger.LogDebug(
                "Hook {HookId} ({HookType}) for {Event} completed in {Duration}ms. Continued={Continued}",
                hook.Id, hook.Type, context.Event, sw.ElapsedMilliseconds, result.Continue);

            return result;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            _logger.LogWarning(
                "Hook {HookId} timed out after {TimeoutMs}ms for event {Event}",
                hook.Id, hook.TimeoutMs, context.Event);
            return HookResult.PassThrough();
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex,
                "Hook {HookId} ({HookType}) failed for event {Event} after {Duration}ms",
                hook.Id, hook.Type, context.Event, sw.ElapsedMilliseconds);
            return HookResult.PassThrough();
        }
    }

    private HookResult ExecutePromptHook(HookDefinition hook, HookExecutionContext context)
    {
        if (string.IsNullOrWhiteSpace(hook.PromptTemplate))
        {
            _logger.LogWarning("Prompt hook {HookId} has no PromptTemplate defined", hook.Id);
            return HookResult.PassThrough();
        }

        var evaluated = hook.PromptTemplate
            .Replace("{ToolName}", context.ToolName ?? string.Empty)
            .Replace("{AgentId}", context.AgentId ?? string.Empty)
            .Replace("{ConversationId}", context.ConversationId ?? string.Empty)
            .Replace("{TurnNumber}", context.TurnNumber?.ToString() ?? string.Empty)
            .Replace("{Event}", context.Event.ToString());

        return new HookResult { AdditionalContext = evaluated };
    }

    private async Task<HookResult> ExecuteHttpHookAsync(
        HookDefinition hook,
        HookExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(hook.WebhookUrl))
        {
            _logger.LogWarning("Http hook {HookId} has no WebhookUrl defined", hook.Id);
            return HookResult.PassThrough();
        }

        // H-2: Block SSRF against internal/reserved networks
        if (!await IsAllowedWebhookUrlAsync(hook.WebhookUrl, cancellationToken))
        {
            _logger.LogWarning(
                "Http hook {HookId} blocked: URL {Url} targets a reserved/internal network",
                hook.Id, hook.WebhookUrl);
            return HookResult.PassThrough();
        }

        var client = _httpClientFactory.CreateClient("HookWebhook");
        var json = JsonSerializer.Serialize(context, JsonOptions);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var response = await client.PostAsync(hook.WebhookUrl, content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<HookResult>(responseBody, JsonOptions);

        return result ?? HookResult.PassThrough();
    }

    private HookResult ExecuteCommandHook(HookDefinition hook)
    {
        _logger.LogWarning(
            "Command hooks deferred for security review. Hook {HookId} was not executed.",
            hook.Id);
        return HookResult.PassThrough();
    }

    private HookResult ExecuteMiddlewareHook(HookDefinition hook)
    {
        _logger.LogWarning(
            "Middleware hooks not yet implemented. Hook {HookId} was not executed.",
            hook.Id);
        return HookResult.PassThrough();
    }

    private void UnregisterRunOnceHooks(IReadOnlyList<HookDefinition> hooks)
    {
        for (var i = 0; i < hooks.Count; i++)
        {
            if (hooks[i].RunOnce)
            {
                _registry.Unregister(hooks[i].Id);
                _logger.LogDebug("Unregistered run-once hook {HookId}", hooks[i].Id);
            }
        }
    }

    /// <summary>
    /// Validates that a webhook URL does not target reserved/internal IP ranges.
    /// Blocks SSRF attacks against cloud metadata services and internal networks.
    /// </summary>
    /// <remarks>
    /// Fail-closed: any URL that cannot be parsed, uses a non-HTTP(S) scheme, names a
    /// known-internal host, or resolves to (or is) a reserved address is rejected.
    /// DNS hostnames are resolved and <em>every</em> returned address is checked so a
    /// hostname whose A/AAAA record points at an internal/metadata address is blocked.
    /// IPv6 literals — including IPv4-mapped (<c>::ffff:a.b.c.d</c>), loopback,
    /// link-local (<c>fe80::/10</c>) and unique-local (<c>fc00::/7</c>) — are all covered.
    /// </remarks>
    private async Task<bool> IsAllowedWebhookUrlAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme is not ("https" or "http"))
            return false;

        if (uri.IsLoopback)
            return false;

        var host = uri.Host;
        if (host is "localhost" or "metadata.google.internal")
            return false;

        // Literal IP host: validate directly, no DNS lookup required.
        if (System.Net.IPAddress.TryParse(host, out var literalIp))
            return !IsReservedAddress(literalIp);

        // DNS hostname: resolve and reject if ANY resolved address is reserved.
        // Resolution failure or an empty result fails closed (rejected).
        System.Net.IPAddress[] resolved;
        try
        {
            resolved = await System.Net.Dns.GetHostAddressesAsync(host, cancellationToken);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
        {
            _logger.LogWarning(
                "Webhook host {Host} could not be resolved for SSRF validation; blocking", host);
            return false;
        }

        if (resolved.Length == 0)
            return false;

        return resolved.All(ip => !IsReservedAddress(ip));
    }

    /// <summary>
    /// Determines whether an IP address falls in a reserved/internal range that must not be
    /// reachable from a webhook (RFC 1918, loopback, link-local incl. cloud metadata, and
    /// the IPv6 equivalents). IPv4-mapped IPv6 addresses are normalized to IPv4 before checking.
    /// </summary>
    private static bool IsReservedAddress(System.Net.IPAddress ip)
    {
        // Normalize ::ffff:a.b.c.d so the IPv4 rules below apply.
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (System.Net.IPAddress.IsLoopback(ip))
            return true;

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            return bytes[0] switch
            {
                10 => true,                                       // 10.0.0.0/8
                127 => true,                                      // 127.0.0.0/8
                169 when bytes[1] == 254 => true,                 // 169.254.0.0/16 (link-local, cloud metadata)
                172 when bytes[1] >= 16 && bytes[1] <= 31 => true, // 172.16.0.0/12
                192 when bytes[1] == 168 => true,                 // 192.168.0.0/16
                _ => false
            };
        }

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal)                               // fe80::/10
                return true;

            // Unique-local addresses fc00::/7 (first 7 bits == 1111110).
            var bytes = ip.GetAddressBytes();
            return (bytes[0] & 0xFE) == 0xFC;
        }

        return false;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}
