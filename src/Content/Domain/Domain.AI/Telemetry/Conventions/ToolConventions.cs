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

    /// <summary>Gen AI operation name for tool execution spans.</summary>
    public const string ExecuteToolOperation = "execute_tool";
    /// <summary>Gen AI span attribute containing the serialized tool call arguments (input).</summary>
    public const string ToolCallArguments = "gen_ai.tool.call.arguments";
    /// <summary>Gen AI span attribute containing the tool call result text.</summary>
    public const string ToolCallResult = "gen_ai.tool.call.result";
    /// <summary>Gen AI span attribute for the operation name.</summary>
    public const string GenAiOperationName = "gen_ai.operation.name";
    /// <summary>Maximum tool result length before truncation.</summary>
    public const int MaxResultLength = 4096;

    /// <summary>Whether the tool returned an empty/null result (bool).</summary>
    public const string ResultEmpty = "agent.tool.result_empty";
    /// <summary>Tool result length in characters.</summary>
    public const string ResultChars = "agent.tool.result_chars";
    /// <summary>Whether the tool result exceeded the truncation threshold (bool).</summary>
    public const string ResultTruncated = "agent.tool.result_truncated";
    /// <summary>Counter: tool calls returning empty results.</summary>
    public const string EmptyResults = "agent.tool.empty_results";
    /// <summary>Histogram: tool result size in characters.</summary>
    public const string ResultSize = "agent.tool.result_size";

    // Causal attribution attributes (Meta-Harness OTel GenAI semantic conventions)

    /// <summary>OTel GenAI semantic convention attribute for tool name (bridged from agent.tool.name).</summary>
    public const string GenAiToolName = "gen_ai.tool.name";
    /// <summary>SHA256 hex digest of serialized tool input arguments. Only set when IsAllDataRequested.</summary>
    public const string InputHash = "tool.input_hash";
    /// <summary>Bucketed outcome category matching ExecutionTraceRecord.result_category.</summary>
    public const string ResultCategory = "tool.result_category";
    /// <summary>CandidateId from TraceScope when running inside an optimization eval.</summary>
    public const string HarnessCandidateId = "gen_ai.harness.candidate_id";
    /// <summary>Iteration number from TraceScope when running inside an optimization eval.</summary>
    public const string HarnessIteration = "gen_ai.harness.iteration";
}
