using Application.AI.Common.Interfaces.AI;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.Services.AI;

/// <summary>
/// Thread-safe, per-execution-context token budget tracker. Seeded from
/// <c>AppConfig.AI.AgentFramework.DefaultTokenBudget</c> at scope creation and decremented
/// as the agent turn consumes tokens.
/// </summary>
/// <remarks>
/// <para>
/// Registered as <strong>scoped</strong>: each MediatR request scope (one agent turn or plan
/// step) gets a fresh budget seeded from configuration. The
/// <see cref="Application.AI.Common.MediatRBehaviors.TokenBudgetBehavior{TRequest,TResponse}"/>
/// performs a pre-flight <see cref="CanAfford"/> check before the handler runs and records
/// actual usage via <see cref="RecordUsage"/> after the turn completes.
/// </para>
/// <para>
/// All mutable state is guarded by a lock so concurrent tool-use callbacks within a single
/// turn observe a consistent remaining budget.
/// </para>
/// </remarks>
public sealed class TokenBudgetTracker : ITokenBudgetTracker
{
    private readonly object _lock = new();
    private readonly ILogger<TokenBudgetTracker> _logger;
    private int _remaining;

    /// <summary>
    /// Initializes a new instance of the <see cref="TokenBudgetTracker"/> class, seeding the
    /// budget from <c>AppConfig.AI.AgentFramework.DefaultTokenBudget</c>.
    /// </summary>
    /// <param name="options">Application configuration supplying the default per-turn budget.</param>
    /// <param name="logger">Logger for budget seeding, consumption, and exhaustion diagnostics.</param>
    public TokenBudgetTracker(IOptionsMonitor<AppConfig> options, ILogger<TokenBudgetTracker> logger)
    {
        _logger = logger;

        var configured = options.CurrentValue.AI.AgentFramework.DefaultTokenBudget;
        TotalBudget = Math.Max(0, configured);
        _remaining = TotalBudget;

        _logger.LogDebug("Token budget tracker seeded with {Total:N0} tokens", TotalBudget);
    }

    /// <inheritdoc />
    public int TotalBudget { get; }

    /// <inheritdoc />
    public int RemainingBudget
    {
        get
        {
            lock (_lock)
            {
                return _remaining;
            }
        }
    }

    /// <inheritdoc />
    public bool CanAfford(int estimatedTokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedTokens);

        lock (_lock)
        {
            return estimatedTokens <= _remaining;
        }
    }

    /// <inheritdoc />
    public void RecordUsage(int tokensUsed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tokensUsed);

        lock (_lock)
        {
            // Clamp at zero: actual usage may legitimately exceed the pre-flight estimate
            // (e.g. unexpected tool-call expansion) and must never push remaining negative.
            _remaining = Math.Max(0, _remaining - tokensUsed);

            if (_remaining == 0 && tokensUsed > 0)
            {
                _logger.LogWarning(
                    "Token budget exhausted after consuming {Tokens:N0} tokens (total {Total:N0})",
                    tokensUsed, TotalBudget);
            }
            else
            {
                _logger.LogDebug(
                    "Recorded {Tokens:N0} tokens; {Remaining:N0}/{Total:N0} remaining",
                    tokensUsed, _remaining, TotalBudget);
            }
        }
    }
}
