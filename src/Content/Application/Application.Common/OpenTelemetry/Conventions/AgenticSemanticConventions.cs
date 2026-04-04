namespace Application.Common.OpenTelemetry.Conventions;

/// <summary>
/// Centralized semantic conventions for all agentic telemetry attributes.
/// Every metric, span tag, and log scope in the harness references these constants
/// to ensure consistent naming across instruments, processors, and dashboards.
/// </summary>
/// <remarks>
/// Follows OpenTelemetry GenAI semantic conventions (<c>gen_ai.*</c>) and extends
/// with <c>agent.*</c> and <c>mcp.*</c> namespaces for agentic-specific concerns.
/// </remarks>
public static class AgenticSemanticConventions
{
    /// <summary>Agent and conversation attributes.</summary>
    public static class Agent
    {
        public const string Name = "agent.name";
        public const string ParentName = "agent.parent_agent.name";
        public const string ConversationId = "agent.conversation.id";
        public const string TurnIndex = "agent.turn.index";
        public const string TurnRole = "agent.turn.role";
    }

    /// <summary>Token usage attributes (supplements <c>gen_ai.usage.*</c>).</summary>
    public static class Tokens
    {
        public const string Input = "agent.tokens.input";
        public const string Output = "agent.tokens.output";
        public const string Total = "agent.tokens.total";
        public const string BudgetLimit = "agent.tokens.budget_limit";
        public const string BudgetUsed = "agent.tokens.budget_used";
        public const string BudgetPercent = "agent.tokens.budget_pct";
    }

    /// <summary>Tool execution attributes and metric names.</summary>
    public static class Tool
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

    /// <summary>MCP server attributes and metric names.</summary>
    public static class Mcp
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

    /// <summary>Content safety attributes and metric names.</summary>
    public static class Safety
    {
        public const string Phase = "agent.safety.phase";
        public const string Filter = "agent.safety.filter";
        public const string Outcome = "agent.safety.outcome";
        public const string Category = "agent.safety.category";
        public const string Severity = "agent.safety.severity";
        public const string Evaluations = "agent.safety.evaluations";
        public const string Blocks = "agent.safety.blocks";

        public static class PhaseValues
        {
            public const string Prompt = "prompt";
            public const string Response = "response";
        }

        public static class OutcomeValues
        {
            public const string Pass = "pass";
            public const string Block = "block";
            public const string Redact = "redact";
        }
    }

    /// <summary>Context budget attributes and metric names.</summary>
    public static class Context
    {
        public const string BudgetLimit = "agent.context.budget_limit";
        public const string BudgetUsed = "agent.context.budget_used";
        public const string CompactionReason = "agent.context.compaction_reason";
        public const string Compactions = "agent.context.compactions";
    }

    /// <summary>Orchestration-level attributes and metric names.</summary>
    public static class Orchestration
    {
        public const string TurnCount = "agent.orchestration.turn_count";
        public const string SubagentCount = "agent.orchestration.subagent_count";
        public const string ToolCallCount = "agent.orchestration.tool_call_count";
        public const string ConversationDuration = "agent.orchestration.conversation_duration";
        public const string TurnsPerConversation = "agent.orchestration.turns_per_conversation";
        public const string SubagentSpawns = "agent.orchestration.subagent_spawns";
    }
}
