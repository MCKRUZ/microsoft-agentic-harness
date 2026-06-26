using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.MediatR;
using Application.AI.Common.Interfaces.WorkMemory;
using Domain.AI.WorkMemory;
using Domain.Common.Config;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.MediatRBehaviors;

/// <summary>
/// Post-turn pipeline behavior that records what the agent <em>did</em> on each turn as a
/// <see cref="WorkEpisode"/> — the capture half of the self-improving work-memory loop. The expensive
/// distillation of episodes into reusable lessons happens later, offline, in the overnight synthesis
/// pass; this behavior only logs, cheaply and structurally, with <strong>no LLM call</strong>.
/// </summary>
/// <remarks>
/// <para>
/// Only activates for requests implementing <see cref="IAgentTurnRequest"/> that produce an
/// <see cref="IAgentTurnResult"/>. Both success and failure are recorded — a failed turn is itself a
/// signal worth learning from. All other request types pass through untouched.
/// </para>
/// <para>
/// Capture runs as fire-and-forget on a background thread; the agent's response returns immediately
/// and capture failures are logged but never propagate. Like <see cref="KnowledgeExtractionBehavior{TRequest, TResponse}"/>,
/// the background task must not capture request-scoped services or the request
/// <see cref="System.Threading.CancellationToken"/>: it injects <see cref="IServiceScopeFactory"/>,
/// creates a fresh DI scope, and re-establishes it as the ambient request scope so the
/// tenant/owner-aware graph store resolves its identity from a live provider.
/// </para>
/// </remarks>
public sealed class WorkEpisodeCaptureBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAmbientRequestScope _ambientScope;
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WorkEpisodeCaptureBehavior<TRequest, TResponse>> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkEpisodeCaptureBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    public WorkEpisodeCaptureBehavior(
        IServiceScopeFactory scopeFactory,
        IAmbientRequestScope ambientScope,
        IOptionsMonitor<AppConfig> appConfig,
        TimeProvider timeProvider,
        ILogger<WorkEpisodeCaptureBehavior<TRequest, TResponse>> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(ambientScope);
        ArgumentNullException.ThrowIfNull(appConfig);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _ambientScope = ambientScope;
        _appConfig = appConfig;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var response = await next();

        // Read live so a hot config change takes effect without evicting anything.
        if (!_appConfig.CurrentValue.AI.WorkMemory.Enabled)
            return response;

        if (request is not IAgentTurnRequest agentRequest || response is not IAgentTurnResult turnResult)
            return response;

        // Snapshot the values the background task needs so the closure captures no request-scoped
        // service and not the request CancellationToken — both are gone once the turn returns.
        var episode = BuildEpisode(agentRequest, turnResult);

        _ = Task.Run(() => PersistAsync(episode));

        return response;
    }

    /// <summary>
    /// Builds a <see cref="WorkEpisode"/> from the turn request and result. Pure and synchronous so it
    /// runs on the request thread (before the closure escapes) and is trivially unit-testable.
    /// </summary>
    internal WorkEpisode BuildEpisode(IAgentTurnRequest request, IAgentTurnResult result)
    {
        var maxChars = _appConfig.CurrentValue.AI.WorkMemory.ResponseSummaryMaxChars;
        var summary = Truncate(result.Response ?? string.Empty, maxChars);

        return new WorkEpisode
        {
            EpisodeId = Guid.NewGuid(),
            AgentId = request.AgentId,
            ConversationId = request.ConversationId,
            TurnNumber = request.TurnNumber,
            UserMessage = request.UserMessage,
            ResponseSummary = summary,
            Outcome = result.Success ? EpisodeOutcome.Success : EpisodeOutcome.Failure,
            InputTokens = result.InputTokens,
            OutputTokens = result.OutputTokens,
            CreatedAt = _timeProvider.GetUtcNow()
        };
    }

    private static string Truncate(string value, int maxChars)
    {
        if (maxChars <= 0 || value.Length <= maxChars)
            return value;

        // Don't split a UTF-16 surrogate pair at the boundary — a lone surrogate can throw or become
        // U+FFFD when the graph backend serializes the string to UTF-8. Back off one char if needed.
        var cut = maxChars;
        if (char.IsHighSurrogate(value[cut - 1]))
            cut--;

        return value[..cut];
    }

    /// <summary>
    /// Persists the episode in a fresh DI scope. Failures are logged and swallowed — work-memory
    /// capture is an enhancement, never a hard dependency of a turn.
    /// </summary>
    private async Task PersistAsync(WorkEpisode episode)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();

            // Re-establish the fresh, alive scope as the ambient request scope so the tenant/owner-aware
            // graph store resolves identity from a live provider (mirrors KnowledgeExtractionBehavior).
            using var _ = _ambientScope.BeginScope(scope.ServiceProvider);

            var store = scope.ServiceProvider.GetRequiredService<IWorkEpisodeStore>();
            var result = await store.SaveAsync(episode, CancellationToken.None);

            if (result.IsSuccess)
                _logger.LogDebug(
                    "Captured work episode {EpisodeId} for conversation {ConversationId} turn {Turn} ({Outcome})",
                    episode.EpisodeId, episode.ConversationId, episode.TurnNumber, episode.Outcome);
            else
                _logger.LogWarning(
                    "Failed to persist work episode for conversation {ConversationId} turn {Turn}: {Errors}",
                    episode.ConversationId, episode.TurnNumber, string.Join(", ", result.Errors));
        }
        catch (Exception ex)
        {
            // Catch everything (incl. OperationCanceledException): this runs on a discarded Task.Run,
            // so an unobserved exception would otherwise escape. Capture is best-effort, never fatal.
            _logger.LogWarning(ex,
                "Work-episode capture failed for conversation {ConversationId} turn {Turn}",
                episode.ConversationId, episode.TurnNumber);
        }
    }
}
