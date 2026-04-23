namespace Domain.AI.Telemetry.Conventions;

/// <summary>Context budget telemetry attributes and metric names.</summary>
public static class ContextConventions
{
    public const string BudgetLimit = "agent.context.budget_limit";
    public const string BudgetUsed = "agent.context.budget_used";
    public const string CompactionReason = "agent.context.compaction_reason";
    public const string Compactions = "agent.context.compactions";

    /// <summary>Token load from the system prompt.</summary>
    public const string SystemPromptTokens = "agent.context.system_prompt_tokens";
    /// <summary>Token load from loaded skills, by tier.</summary>
    public const string SkillsLoadedTokens = "agent.context.skills_loaded_tokens";
    /// <summary>Skill loading tier dimension (1=Index Card, 2=Folder, 3=Filing Cabinet).</summary>
    public const string SkillsTier = "agent.context.skills_tier";
    /// <summary>Token load from tool JSON schemas sent to the LLM.</summary>
    public const string ToolsSchemaTokens = "agent.context.tools_schema_tokens";
    /// <summary>Remaining token budget for the agent session.</summary>
    public const string BudgetRemaining = "agent.context.budget_remaining";
    /// <summary>Budget utilization ratio (0-1, used/limit).</summary>
    public const string BudgetUtilization = "agent.context.budget_utilization";

    /// <summary>Token count broken down by context source type. Tags: agent.context.source_type, agent.name.</summary>
    public const string SourceTokens = "agent.context.source_tokens";
    /// <summary>Context source type dimension label.</summary>
    public const string SourceType = "agent.context.source_type";

    public static class SourceTypeValues
    {
        public const string SystemPrompt = "system_prompt";
        public const string Skills = "skills";
        public const string ToolsSchema = "tools_schema";
        public const string Hooks = "hooks";
        public const string UserMessage = "user_message";
        public const string ToolResult = "tool_result";
        public const string AssistantResponse = "assistant_response";
    }
}
