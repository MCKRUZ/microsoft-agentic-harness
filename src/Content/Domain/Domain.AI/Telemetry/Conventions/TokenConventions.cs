namespace Domain.AI.Telemetry.Conventions;

/// <summary>Token usage telemetry attributes.</summary>
public static class TokenConventions
{
    public const string Input = "agent.tokens.input";
    public const string Output = "agent.tokens.output";
    public const string Total = "agent.tokens.total";
    public const string BudgetLimit = "agent.tokens.budget_limit";
    public const string BudgetUsed = "agent.tokens.budget_used";
    public const string BudgetPercent = "agent.tokens.budget_pct";

    /// <summary>Cache-read input tokens per LLM call (prompt cache hits).</summary>
    public const string CacheRead = "agent.tokens.cache_read";
    /// <summary>Cache-write (creation) input tokens per LLM call.</summary>
    public const string CacheWrite = "agent.tokens.cache_write";
    /// <summary>Estimated cost in USD per LLM call.</summary>
    public const string CostEstimated = "agent.tokens.cost_estimated";
    /// <summary>Estimated cost savings from cache hits in USD.</summary>
    public const string CostCacheSavings = "agent.tokens.cost_cache_savings";
    /// <summary>Cache hit rate per LLM call (0-1 ratio of cache-read to total input).</summary>
    public const string CacheHitRate = "agent.tokens.cache_hit_rate";
    /// <summary>Estimated cost per conversation turn in USD.</summary>
    public const string CostPerTurn = "agent.tokens.cost_per_turn";
    /// <summary>Total tokens consumed per conversation turn.</summary>
    public const string TokensPerTurn = "agent.tokens.tokens_per_turn";

    /// <summary>Gen AI semantic convention: input tokens reported by the SDK.</summary>
    public const string GenAiInputTokens = "gen_ai.usage.input_tokens";
    /// <summary>Gen AI semantic convention: output tokens reported by the SDK.</summary>
    public const string GenAiOutputTokens = "gen_ai.usage.output_tokens";
    /// <summary>Gen AI semantic convention: cache-read input tokens.</summary>
    public const string GenAiCacheReadTokens = "gen_ai.usage.cache_read_input_tokens";
    /// <summary>Gen AI semantic convention: cache-creation input tokens.</summary>
    public const string GenAiCacheWriteTokens = "gen_ai.usage.cache_creation_input_tokens";
    /// <summary>Gen AI semantic convention: requested model identifier.</summary>
    public const string GenAiRequestModel = "gen_ai.request.model";
}
