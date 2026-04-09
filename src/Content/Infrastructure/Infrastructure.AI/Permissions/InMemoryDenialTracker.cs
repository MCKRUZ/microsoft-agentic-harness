using System.Collections.Concurrent;
using Application.AI.Common.Interfaces.Permissions;
using Domain.AI.Permissions;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Permissions;

/// <summary>
/// In-memory implementation of <see cref="IDenialTracker"/> that tracks permission denials
/// per agent using concurrent dictionaries. Thread-safe for multi-agent scenarios.
/// </summary>
/// <remarks>
/// <para>
/// Denial state is keyed by agent ID (outer dictionary) and a composite
/// <c>{toolName}:{operation ?? "*"}</c> key (inner dictionary). When the denial count
/// for any key reaches <see cref="Domain.Common.Config.AI.Permissions.PermissionsConfig.DenialRateLimitThreshold"/>,
/// future requests for that tool+operation are auto-denied.
/// </para>
/// <para>State is ephemeral and lost on application restart.</para>
/// </remarks>
public sealed class InMemoryDenialTracker : IDenialTracker
{
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly ILogger<InMemoryDenialTracker> _logger;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DenialState>> _denials = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryDenialTracker"/> class.
    /// </summary>
    /// <param name="options">Configuration monitor providing the denial rate limit threshold.</param>
    /// <param name="logger">Logger for denial tracking events.</param>
    public InMemoryDenialTracker(
        IOptionsMonitor<AppConfig> options,
        ILogger<InMemoryDenialTracker> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public void RecordDenial(string agentId, string toolName, string? operation = null)
    {
        var agentDenials = _denials.GetOrAdd(agentId, _ => new ConcurrentDictionary<string, DenialState>());
        var key = BuildKey(toolName, operation);
        var now = DateTimeOffset.UtcNow;

        var state = agentDenials.AddOrUpdate(
            key,
            _ => new DenialState(1, now, now),
            (_, existing) => existing with { Count = existing.Count + 1, LastDenied = now });

        var threshold = _options.CurrentValue.AI.Permissions.DenialRateLimitThreshold;

        if (state.Count == threshold)
        {
            _logger.LogWarning(
                "Agent {AgentId} has been denied tool {ToolName} (operation: {Operation}) {Count} times — now rate-limited",
                agentId, toolName, operation ?? "*", state.Count);
        }
    }

    /// <inheritdoc />
    public bool IsRateLimited(string agentId, string toolName, string? operation = null)
    {
        if (!_denials.TryGetValue(agentId, out var agentDenials))
            return false;

        var key = BuildKey(toolName, operation);

        if (!agentDenials.TryGetValue(key, out var state))
            return false;

        var threshold = _options.CurrentValue.AI.Permissions.DenialRateLimitThreshold;
        return state.Count >= threshold;
    }

    /// <inheritdoc />
    public IReadOnlyList<DenialRecord> GetDenials(string agentId)
    {
        if (!_denials.TryGetValue(agentId, out var agentDenials))
            return [];

        return agentDenials.Select(kvp =>
        {
            var (toolName, operation) = ParseKey(kvp.Key);
            return new DenialRecord
            {
                ToolName = toolName,
                OperationPattern = operation,
                DenialCount = kvp.Value.Count,
                FirstDenied = kvp.Value.FirstDenied,
                LastDenied = kvp.Value.LastDenied
            };
        }).ToList();
    }

    /// <inheritdoc />
    public void Reset(string agentId)
    {
        _denials.TryRemove(agentId, out _);
    }

    private static string BuildKey(string toolName, string? operation) =>
        $"{toolName}:{operation ?? "*"}";

    private static (string ToolName, string? Operation) ParseKey(string key)
    {
        var separatorIndex = key.LastIndexOf(':');
        var toolName = key[..separatorIndex];
        var operation = key[(separatorIndex + 1)..];
        return (toolName, operation == "*" ? null : operation);
    }

    /// <summary>
    /// Internal state tracking denial count and timestamps for a single tool+operation pattern.
    /// </summary>
    private sealed record DenialState(int Count, DateTimeOffset FirstDenied, DateTimeOffset LastDenied);
}
