using System.Text.Json;
using Application.AI.Common.Interfaces.Memory;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Models;

namespace Infrastructure.AI.Tools;

/// <summary>
/// Tool keyed <c>"read_history"</c>. Queries the agent decision log for a specific
/// execution run and returns matching events as a JSON array.
/// </summary>
/// <remarks>
/// <para>
/// Safe to call with unknown or nonexistent run IDs — returns <c>"[]"</c> rather than throwing.
/// </para>
/// <para>
/// <strong>Parameters (passed via <c>execution</c> operation):</strong>
/// <list type="bullet">
///   <item><c>execution_run_id</c> (string, required) — which run to query</item>
///   <item><c>event_type</c> (string, optional) — filter by event type</item>
///   <item><c>tool_name</c> (string, optional) — filter by tool name</item>
///   <item><c>since</c> (long, optional, default 0) — sequence checkpoint; only events after this sequence are returned</item>
///   <item><c>limit</c> (int, optional, default 100) — max results</item>
/// </list>
/// </para>
/// </remarks>
public sealed class ReadHistoryTool : ITool
{
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    private readonly IAgentHistoryStore _historyStore;

    /// <summary>Initializes a new instance of <see cref="ReadHistoryTool"/>.</summary>
    public ReadHistoryTool(IAgentHistoryStore historyStore)
    {
        _historyStore = historyStore;
    }

    /// <inheritdoc />
    public string Name => "read_history";

    /// <inheritdoc />
    public string Description =>
        "Query the agent decision log for a specific execution run. " +
        "Returns a JSON array of decision events filtered by run ID, event type, tool name, " +
        "and sequence checkpoint. Returns '[]' for unknown run IDs.";

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedOperations { get; } = ["query"];

    /// <inheritdoc />
    public bool IsReadOnly => true;

    /// <inheritdoc />
    public bool IsConcurrencySafe => true;

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        string operation,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(operation, "query", StringComparison.Ordinal))
            return ToolResult.Fail($"ReadHistoryTool does not support operation '{operation}'. Supported: query");

        var runId = GetString(parameters, "execution_run_id");
        if (string.IsNullOrEmpty(runId))
            return ToolResult.Ok("[]");

        var query = new DecisionLogQuery
        {
            ExecutionRunId = runId,
            EventType = GetString(parameters, "event_type"),
            ToolName = GetString(parameters, "tool_name"),
            Since = GetLong(parameters, "since"),
            Limit = GetInt(parameters, "limit", 100)
        };

        var events = new List<AgentDecisionEvent>();
        await foreach (var evt in _historyStore.QueryAsync(query, cancellationToken))
            events.Add(evt);

        return ToolResult.Ok(JsonSerializer.Serialize(events, SerializeOptions));
    }

    private static string? GetString(IReadOnlyDictionary<string, object?> p, string key) =>
        p.TryGetValue(key, out var v) ? v?.ToString() : null;

    private static long GetLong(IReadOnlyDictionary<string, object?> p, string key)
    {
        if (!p.TryGetValue(key, out var v) || v is null) return 0L;
        return v is long l ? l : long.TryParse(v.ToString(), out var parsed) ? parsed : 0L;
    }

    private static int GetInt(IReadOnlyDictionary<string, object?> p, string key, int defaultValue)
    {
        if (!p.TryGetValue(key, out var v) || v is null) return defaultValue;
        return v is int i ? i : int.TryParse(v.ToString(), out var parsed) ? parsed : defaultValue;
    }
}
