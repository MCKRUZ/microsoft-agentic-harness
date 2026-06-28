using System.Text.Json;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Models;

namespace Infrastructure.AI.Tools;

/// <summary>
/// Renders a chart inline in the agent's chat answer ("generative UI"): the agent picks <em>what</em>
/// to chart, and the browser renders one of the dashboard's existing chart components populated via
/// the existing Prometheus query path. The browser returns a short textual data summary so the agent
/// can narrate what it drew.
/// </summary>
/// <remarks>
/// <para>
/// A client round-trip tool using the same blocking-proxy mechanism as <see cref="DashboardControlTool"/>:
/// it delegates the render to the connected browser via <see cref="IClientToolBridge"/> and returns the
/// browser's summary. The chart itself never leaves the client; only the summary flows back to the model.
/// Use <c>list_metrics</c> first to discover valid metric ids.
/// </para>
/// <para>
/// Register via keyed DI:
/// <code>
/// services.AddKeyedSingleton&lt;ITool&gt;(RenderChartTool.ToolName, (sp, _) =&gt;
///     new RenderChartTool(sp.GetRequiredService&lt;IClientToolBridge&gt;()));
/// </code>
/// </para>
/// </remarks>
public sealed class RenderChartTool : BlockingProxyTool
{
    /// <summary>The tool name matching keyed DI registration and SKILL.md declarations.</summary>
    public const string ToolName = "render_chart";

    private const string Render = "render";

    private static readonly IReadOnlyList<string> Operations = [Render];

    /// <summary>Initializes a new instance of the <see cref="RenderChartTool"/> class.</summary>
    /// <param name="bridge">The client round-trip bridge used to delegate rendering to the browser.</param>
    public RenderChartTool(IClientToolBridge bridge) : base(bridge)
    {
    }

    /// <inheritdoc />
    public override string Name => ToolName;

    /// <inheritdoc />
    public override string Description =>
        "Renders a chart inline in your answer from the dashboard's metrics. Operation: render. " +
        "Provide either a metricId from list_metrics (preferred) or a raw promQL query. " +
        "Parameters: metricId (string, optional — a metric id such as 'tokens_by_model'); " +
        "promQL (string, optional — a Prometheus query, used when no metricId is given); " +
        "chartType (string, optional — 'timeseries', 'bar', or 'pie'; defaults to the metric's type); " +
        "title (string, optional — a heading for the chart). The chart uses the dashboard's current " +
        "time range, so call set_time_range first if a different window is needed.";

    /// <inheritdoc />
    public override IReadOnlyList<string> SupportedOperations => Operations;

    /// <inheritdoc />
    public override Task<ToolResult> ExecuteAsync(
        string operation,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(operation, Render, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(ToolResult.Fail($"Unknown operation: {operation}. Supported: {Render}"));

        if (!IsClientAttached)
            return Task.FromResult(ToolResult.Fail("No dashboard client is connected to this conversation, so a chart cannot be rendered."));

        var hasMetric = parameters.ContainsKey("metricId") || parameters.ContainsKey("promQL");
        if (!hasMetric)
            return Task.FromResult(ToolResult.Fail("Provide a metricId (from list_metrics) or a promQL query to chart."));

        var argumentsJson = JsonSerializer.Serialize(parameters, SerializerOptions);

        return InvokeClientAsync(argumentsJson, "The dashboard did not render the chart in time.", cancellationToken);
    }
}
