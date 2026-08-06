namespace Domain.Common.Config.AI;

/// <summary>
/// Configuration for the AI agent framework provider and default deployment settings.
/// Bound from <c>AppConfig:AI:AgentFramework</c> in appsettings.json.
/// </summary>
public class AgentFrameworkConfig
{
    /// <summary>
    /// Gets or sets the provider endpoint URL.
    /// For Azure OpenAI: <c>https://your-resource.openai.azure.com/</c>.
    /// For OpenAI: leave empty (uses default endpoint).
    /// Store in User Secrets or Key Vault — never in appsettings.json.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// Gets or sets the API key for the provider.
    /// Store in User Secrets or Key Vault — never in appsettings.json.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Gets or sets the default deployment/model name used when no override is specified.
    /// </summary>
    public string DefaultDeployment { get; set; } = "default";

    /// <summary>
    /// Gets or sets the authoritative list of deployment/model names a caller may request
    /// as a per-conversation override. When empty, consumers should treat
    /// <c>[<see cref="DefaultDeployment"/>]</c> as the single available option.
    /// </summary>
    public List<string> AvailableDeployments { get; set; } = [];

    /// <summary>
    /// Gets or sets the default AI framework client type.
    /// Determines which provider is used when no override is specified per-skill or per-agent.
    /// </summary>
    public AIAgentFrameworkClientType ClientType { get; set; } = AIAgentFrameworkClientType.AzureOpenAI;

    /// <summary>
    /// Gets or sets the default per-turn token budget enforced for a single agent execution
    /// context (one agent turn or plan step). Seeds the request-scoped
    /// <c>ITokenBudgetTracker</c> at the start of each request scope. A pre-flight check
    /// rejects any token-consuming request whose estimated cost exceeds the remaining budget;
    /// actual usage is decremented after the turn.
    /// </summary>
    /// <remarks>
    /// Defaults to 200,000 tokens — a conservative ceiling that accommodates multi-step
    /// tool-call chains on large-context models while still guarding against runaway loops.
    /// Tune per deployment via <c>AppConfig:AI:AgentFramework:DefaultTokenBudget</c>.
    /// </remarks>
    public int DefaultTokenBudget { get; set; } = 200_000;

    /// <summary>
    /// Gets or sets the conversation-lifetime token budget — the cumulative input+output token ceiling
    /// across <em>all</em> turns of a single conversation. When a conversation reaches this ceiling, the
    /// next turn is refused gracefully (the loop breaks / the live turn is declined with a message)
    /// rather than throwing. Distinct from <see cref="DefaultTokenBudget"/>, which caps a single turn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Defaults to 1,000,000 tokens — roughly 50–100 exchanges once each turn's resent history is counted,
    /// which is a long support or voice session rather than a limit an ordinary conversation reaches. It is
    /// a runaway guard, not a business rule: work that loops or is never closed stops at a bounded cost
    /// instead of billing indefinitely.
    /// </para>
    /// <para>
    /// <strong>The default has to be positive.</strong> A durable conversation outlives any single run, so
    /// the per-run caps (<c>maxTurns</c> and the seed-message cap) deliberately do not bound it — this
    /// budget is the only thing that does, and the Execution API contract says so. Set it to <c>0</c> to
    /// disable, and that is what <em>unbounded</em> means: nothing else is watching.
    /// </para>
    /// <para>
    /// <strong>Applies per budget key, of which there are two kinds.</strong> Conversational turns key by
    /// conversation, and an exhausted conversation declines further turns gracefully with an explanatory
    /// message — no model call, no error. Plan execution keys by plan run instead, and an exhausted run
    /// fails the step as a policy denial. Same ceiling, two units and two failure shapes.
    /// </para>
    /// <para>
    /// Enforced by <c>IConversationBudgetTracker</c> between turns, never mid-turn — a turn already in
    /// flight always finishes, and its own cost is bounded by <see cref="DefaultTokenBudget"/>.
    /// </para>
    /// </remarks>
    public int ConversationTokenBudget { get; set; } = 1_000_000;

    /// <summary>
    /// Gets or sets whether to enable Anthropic prompt caching on the OpenAI-compatible client path
    /// (e.g. Claude via OpenRouter). When true, a request-pipeline policy stamps a
    /// <c>cache_control</c> breakpoint on the stable system prefix of each chat-completions call so
    /// the provider caches and reuses it, cutting the cost of the repeated prefix by ~90%.
    /// </summary>
    /// <remarks>
    /// Off by default — it is a no-op (and a small net cost from the cache-write premium) for
    /// single-shot calls or prefixes below the provider's minimum cacheable size, so consumers opt
    /// in deliberately. Only affects the <see cref="AIAgentFrameworkClientType.OpenAI"/> path; other
    /// providers (Azure OpenAI caches automatically; native Anthropic uses a different mechanism)
    /// ignore this flag.
    /// </remarks>
    public bool EnablePromptCaching { get; set; }

    /// <summary>
    /// Returns true when minimum configuration is present to create a chat client.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}
