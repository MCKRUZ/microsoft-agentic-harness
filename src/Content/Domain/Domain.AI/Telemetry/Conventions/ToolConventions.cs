namespace Domain.AI.Telemetry.Conventions;

/// <summary>Tool execution telemetry attributes and metric names.</summary>
public static class ToolConventions
{
    public const string Name = "agent.tool.name";
    public const string Source = "agent.tool.source";
    public const string Status = "agent.tool.status";
    public const string ErrorType = "agent.tool.error_type";
    public const string Duration = "agent.tool.duration";
    public const string Invocations = "agent.tool.invocations";
    public const string Errors = "agent.tool.errors";

    public static class StatusValues
    {
        public const string Success = "success";
        public const string Failure = "failure";
        public const string Timeout = "timeout";
    }

    public static class SourceValues
    {
        public const string KeyedDI = "keyed_di";
        public const string Mcp = "mcp";
        public const string SemanticKernel = "semantic_kernel";
    }
}
