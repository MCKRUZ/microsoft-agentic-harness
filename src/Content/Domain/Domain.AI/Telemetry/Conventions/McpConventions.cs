namespace Domain.AI.Telemetry.Conventions;

/// <summary>MCP server telemetry attributes and metric names.</summary>
public static class McpConventions
{
    public const string ServerName = "mcp.server.name";
    public const string Operation = "mcp.server.operation";
    public const string Status = "mcp.server.status";
    public const string ErrorType = "mcp.server.error_type";
    public const string RequestDuration = "mcp.server.request_duration";
    public const string Requests = "mcp.server.requests";

    public static class StatusValues
    {
        public const string Available = "available";
        public const string Unavailable = "unavailable";
        public const string Error = "error";
    }
}
