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
public sealed class RenderChartTool : SingleRenderProxyTool
{
    /// <summary>The tool name matching keyed DI registration and SKILL.md declarations.</summary>
    public const string ToolName = "render_chart";

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
    protected override string NoClientMessage =>
        "No dashboard client is connected to this conversation, so a chart cannot be rendered.";

    /// <inheritdoc />
    protected override string TimeoutMessage => "The dashboard did not render the chart in time.";

    /// <inheritdoc />
    // Either a metricId or a promQL query must be present; both are top-level scalars, so check the
    // parameter dictionary directly.
    protected override string? ValidateArguments(
        IReadOnlyDictionary<string, object?> parameters, string argumentsJson)
    {
        var hasMetric = parameters.ContainsKey("metricId") || parameters.ContainsKey("promQL");
        return hasMetric ? null : "Provide a metricId (from list_metrics) or a promQL query to chart.";
    }
}
