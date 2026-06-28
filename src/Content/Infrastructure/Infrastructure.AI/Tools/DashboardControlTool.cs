using System.Text.Json;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Models;

namespace Infrastructure.AI.Tools;

/// <summary>
/// Lets an agent act on the connected dashboard UI — read the current view, change the time range,
/// navigate, or refresh data — by delegating each operation to the browser through an
/// <see cref="IClientToolBridge"/> round-trip and returning the browser's result to the model.
/// </summary>
/// <remarks>
/// <para>
/// This is a server-defined, governed action set (deliberately not a browser-advertised dynamic tool):
/// the operations are fixed and validated here, while the actual effect is carried out by the client
/// that owns the dashboard state. The tool blocks for the duration of one client round-trip; the
/// <see cref="IClientToolBridge"/> enforces the bounded timeout and honors run cancellation.
/// </para>
/// <para>
/// Register via keyed DI:
/// <code>
/// services.AddKeyedSingleton&lt;ITool&gt;(DashboardControlTool.ToolName, (sp, _) =&gt;
///     new DashboardControlTool(sp.GetRequiredService&lt;IClientToolBridge&gt;()));
/// </code>
/// </para>
/// </remarks>
public sealed class DashboardControlTool : BlockingProxyTool
{
    /// <summary>The tool name matching keyed DI registration and SKILL.md declarations.</summary>
    public const string ToolName = "dashboard_control";

    private const string GetState = "get_state";
    private const string SetTimeRange = "set_time_range";
    private const string Navigate = "navigate";
    private const string RefreshData = "refresh_data";

    private static readonly IReadOnlyList<string> Operations =
        [GetState, SetTimeRange, Navigate, RefreshData];

    /// <summary>Initializes a new instance of the <see cref="DashboardControlTool"/> class.</summary>
    /// <param name="bridge">The client round-trip bridge used to delegate operations to the browser.</param>
    public DashboardControlTool(IClientToolBridge bridge) : base(bridge)
    {
    }

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        "Acts on the user's live dashboard: read the current view (get_state), change the time range " +
        "(set_time_range with a preset like '24h'/'7d' or a custom from/to), navigate to a page " +
        "(navigate with a path), or refresh the current data (refresh_data). The action runs in the " +
        "user's browser and returns a short result.";

    /// <inheritdoc />
    public override IReadOnlyList<string> SupportedOperations => Operations;

    /// <inheritdoc />
    public override Task<ToolResult> ExecuteAsync(
        string operation,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        var op = operation?.ToLowerInvariant();
        if (op is null || !Operations.Contains(op))
            return Task.FromResult(ToolResult.Fail($"Unknown operation: {operation}. Supported: {string.Join(", ", Operations)}"));

        if (!IsClientAttached)
            return Task.FromResult(ToolResult.Fail("No dashboard client is connected to this conversation, so the view cannot be controlled."));

        var argumentsJson = JsonSerializer.Serialize(new DashboardControlPayload(op, parameters), SerializerOptions);

        return InvokeClientAsync(
            argumentsJson,
            "The dashboard did not respond in time; the action may not have been applied.",
            cancellationToken);
    }

    /// <summary>The wire payload posted to the browser: the operation plus its raw parameters.</summary>
    private sealed record DashboardControlPayload(
        string Operation,
        IReadOnlyDictionary<string, object?> Parameters);
}
