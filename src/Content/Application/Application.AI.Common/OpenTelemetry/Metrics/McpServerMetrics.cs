using System.Diagnostics.Metrics;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Telemetry;

namespace Application.AI.Common.OpenTelemetry.Metrics;

/// <summary>
/// OTel metric instruments for tracking MCP client-side health — request latency
/// and success/failure counts per MCP server consumed by the harness.
/// </summary>
/// <remarks>
/// These are <strong>client-side</strong> metrics for external MCP servers the harness connects to.
/// The MCP server project (<c>Infrastructure.AI.MCPServer</c>) has its own ASP.NET Core
/// instrumentation for server-side metrics.
/// </remarks>
public static class McpServerMetrics
{
    /// <summary>Per-operation latency. Tags: mcp.server.name, mcp.server.operation, mcp.server.status.</summary>
    public static Histogram<double> RequestDuration { get; } =
        AppInstrument.Meter.CreateHistogram<double>(McpConventions.RequestDuration, "ms", "MCP server request latency");

    /// <summary>Request count. Tags: mcp.server.name, mcp.server.operation, mcp.server.status.</summary>
    public static Counter<long> Requests { get; } =
        AppInstrument.Meter.CreateCounter<long>(McpConventions.Requests, "{request}", "MCP server request count");
}
