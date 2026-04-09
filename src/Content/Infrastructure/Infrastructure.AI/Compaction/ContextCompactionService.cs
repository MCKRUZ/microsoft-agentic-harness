using Application.AI.Common.Interfaces.Compaction;
using Application.AI.Common.Interfaces.Hooks;
using Application.AI.Common.Interfaces.Prompts;
using Domain.AI.Compaction;
using Domain.AI.Hooks;
using Domain.Common.Config;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Compaction;

/// <summary>
/// Orchestrates context compaction by selecting the appropriate strategy executor,
/// firing lifecycle hooks, invalidating prompt caches, and managing circuit breaker state.
/// </summary>
public sealed class ContextCompactionService : IContextCompactionService
{
    private readonly IReadOnlyDictionary<CompactionStrategy, ICompactionStrategyExecutor> _strategies;
    private readonly IHookExecutor _hookExecutor;
    private readonly ISystemPromptComposer _promptComposer;
    private readonly IAutoCompactStateMachine _stateMachine;
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly ILogger<ContextCompactionService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="ContextCompactionService"/>.
    /// </summary>
    /// <param name="strategies">All registered compaction strategy executors.</param>
    /// <param name="hookExecutor">Hook executor for PreCompact/PostCompact lifecycle events.</param>
    /// <param name="promptComposer">System prompt composer whose cache is invalidated after compaction.</param>
    /// <param name="stateMachine">Circuit breaker state machine for auto-compact tracking.</param>
    /// <param name="options">Application configuration containing compaction settings.</param>
    /// <param name="logger">Logger for compaction operations.</param>
    public ContextCompactionService(
        IEnumerable<ICompactionStrategyExecutor> strategies,
        IHookExecutor hookExecutor,
        ISystemPromptComposer promptComposer,
        IAutoCompactStateMachine stateMachine,
        IOptionsMonitor<AppConfig> options,
        ILogger<ContextCompactionService> logger)
    {
        _strategies = strategies.ToDictionary(s => s.Strategy);
        _hookExecutor = hookExecutor;
        _promptComposer = promptComposer;
        _stateMachine = stateMachine;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<CompactionResult> CompactAsync(
        string agentId,
        IReadOnlyList<ChatMessage> messages,
        CompactionStrategy strategy,
        CancellationToken cancellationToken = default)
    {
        if (!_strategies.TryGetValue(strategy, out var executor))
        {
            _logger.LogError("No compaction strategy executor registered for {Strategy}", strategy);
            return CompactionResult.Failed($"No executor registered for strategy '{strategy}'.");
        }

        _logger.LogInformation(
            "Starting {Strategy} compaction for agent {AgentId} with {MessageCount} messages",
            strategy, agentId, messages.Count);

        var preContext = new HookExecutionContext
        {
            Event = HookEvent.PreCompact,
            AgentId = agentId
        };

        await _hookExecutor.ExecuteHooksAsync(HookEvent.PreCompact, preContext, cancellationToken);

        var result = await executor.ExecuteAsync(agentId, messages, cancellationToken);

        if (result.Success)
        {
            var postContext = new HookExecutionContext
            {
                Event = HookEvent.PostCompact,
                AgentId = agentId
            };

            await _hookExecutor.ExecuteHooksAsync(HookEvent.PostCompact, postContext, cancellationToken);
            _promptComposer.InvalidateAll();
            _stateMachine.RecordSuccess(agentId);

            _logger.LogInformation(
                "Compaction succeeded for agent {AgentId}: saved {TokensSaved} tokens ({Strategy})",
                agentId, result.Boundary?.TokensSaved ?? 0, strategy);
        }
        else
        {
            _stateMachine.RecordFailure(agentId);

            _logger.LogWarning(
                "Compaction failed for agent {AgentId}: {Error} ({Strategy})",
                agentId, result.Error, strategy);
        }

        return result;
    }

    /// <inheritdoc />
    public bool ShouldAutoCompact(string agentId, int currentTokens, int maxTokens)
    {
        if (_stateMachine.IsCircuitBroken(agentId))
        {
            _logger.LogDebug(
                "Auto-compact skipped for agent {AgentId}: circuit breaker is open",
                agentId);
            return false;
        }

        var threshold = _options.CurrentValue.AI.ContextManagement.Compaction.AutoCompactThresholdRatio;
        return currentTokens >= maxTokens * threshold;
    }
}
