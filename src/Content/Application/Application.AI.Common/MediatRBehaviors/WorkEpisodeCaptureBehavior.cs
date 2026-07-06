using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.MediatR;
using Application.AI.Common.Interfaces.WorkMemory;
using Domain.AI.KnowledgeGraph.Models;
using Domain.AI.WorkMemory;
using Domain.Common.Config;
using Domain.Common.Config.AI.HarmonicMemory;
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
/// This is a <strong>shared turn-boundary capture seam</strong>. One interception emits up to two distinct
/// records: the truncated <see cref="WorkEpisode"/> (when work memory is enabled) and, when harmonic memory
/// is enabled (<c>AppConfig:AI:HarmonicMemory:Mode</c> not <see cref="HarmonicMemoryMode.Off"/>), a raw,
/// untruncated <see cref="EpisodicSegment"/> for retrieval grounding. The two stay distinct records —
/// merging is lossy both ways — cross-linked by <see cref="EpisodicSegment.EpisodeId"/> and the
/// <c>(ConversationId, TurnNumber)</c> pair. The seam fires when <em>either</em> subsystem is on; each
/// output is gated independently.
/// </para>
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

        // Read live so a hot config change takes effect without evicting anything. The seam is shared:
        // it fires when either the work-memory or the harmonic episodic-memory subsystem is enabled.
        var ai = _appConfig.CurrentValue.AI;
        var workMemoryEnabled = ai.WorkMemory.Enabled;
        var harmonicEnabled = ai.HarmonicMemory.Mode != HarmonicMemoryMode.Off;
        if (!workMemoryEnabled && !harmonicEnabled)
            return response;

        if (request is not IAgentTurnRequest agentRequest || response is not IAgentTurnResult turnResult)
            return response;

        // Snapshot the values the background task needs so the closure captures no request-scoped
        // service and not the request CancellationToken — both are gone once the turn returns. The episode
        // is always built (pure, cheap): it is the WorkEpisode payload when work memory is on, and its id +
        // timestamp anchor the episodic segment's cross-link when harmonic episodic capture is on.
        var episode = BuildEpisode(agentRequest, turnResult);
        var segment = harmonicEnabled
            // Stamp the episode cross-link only when the episode is actually persisted; otherwise it would
            // dangle. The durable correlation is always (ConversationId, TurnNumber).
            ? BuildSegment(agentRequest, turnResult, workMemoryEnabled ? episode.EpisodeId : null, episode.CreatedAt)
            : null;

        _ = Task.Run(() => PersistAsync(episode, workMemoryEnabled, segment));

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

    /// <summary>
    /// Builds a raw <see cref="EpisodicSegment"/> from the turn, cross-linked to the same turn's
    /// <see cref="WorkEpisode"/> via <paramref name="episodeId"/> and sharing its <paramref name="createdAt"/>.
    /// Pure and synchronous so it runs on the request thread (before the closure escapes) and is trivially
    /// unit-testable. Unlike <see cref="BuildEpisode"/>, the content is <strong>not truncated</strong> — the
    /// grounding value lives in the verbatim specifics.
    /// </summary>
    internal EpisodicSegment BuildSegment(
        IAgentTurnRequest request,
        IAgentTurnResult result,
        Guid? episodeId,
        DateTimeOffset createdAt) =>
        new()
        {
            SegmentId = Guid.NewGuid(),
            EpisodeId = episodeId,
            AgentId = request.AgentId,
            ConversationId = request.ConversationId,
            TurnNumber = request.TurnNumber,
            Content = $"User: {request.UserMessage}\nAssistant: {result.Response ?? string.Empty}",
            CreatedAt = createdAt
        };

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
    /// Persists the turn's records in a single fresh DI scope: the <see cref="WorkEpisode"/> when
    /// <paramref name="storeEpisode"/> is set, and the raw <see cref="EpisodicSegment"/> when
    /// <paramref name="segment"/> is non-null. Failures are logged and swallowed — turn-boundary capture is
    /// an enhancement, never a hard dependency of a turn. A failure of one record does not abort the other.
    /// </summary>
    private async Task PersistAsync(WorkEpisode episode, bool storeEpisode, EpisodicSegment? segment)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();

            // Re-establish the fresh, alive scope as the ambient request scope so the tenant/owner-aware
            // graph store resolves identity from a live provider (mirrors KnowledgeExtractionBehavior).
            using var _ = _ambientScope.BeginScope(scope.ServiceProvider);

            // The two writes are independent: each is self-contained so a throw from one (e.g. a store
            // resolution failure) never aborts the other. Both subsystems are enhancements, never fatal.
            if (storeEpisode)
                await PersistEpisodeAsync(scope.ServiceProvider, episode);

            if (segment is not null)
                await PersistSegmentAsync(scope.ServiceProvider, segment);
        }
        catch (Exception ex)
        {
            // Backstop: BeginScope/CreateAsyncScope failures, or anything the per-record catches missed.
            // This runs on a discarded Task.Run, so an unobserved exception would otherwise escape.
            _logger.LogWarning(ex,
                "Turn-boundary capture failed for conversation {ConversationId} turn {Turn}",
                episode.ConversationId, episode.TurnNumber);
        }
    }

    private async Task PersistEpisodeAsync(IServiceProvider provider, WorkEpisode episode)
    {
        try
        {
            var store = provider.GetRequiredService<IWorkEpisodeStore>();
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
            // Isolate the episode write so a failure here still lets the episodic segment persist.
            _logger.LogWarning(ex,
                "Work-episode capture failed for conversation {ConversationId} turn {Turn}",
                episode.ConversationId, episode.TurnNumber);
        }
    }

    private async Task PersistSegmentAsync(IServiceProvider provider, EpisodicSegment segment)
    {
        try
        {
            var store = provider.GetRequiredService<IEpisodicSegmentStore>();
            var result = await store.SaveAsync(segment, CancellationToken.None);

            if (result.IsSuccess)
                _logger.LogDebug(
                    "Captured episodic segment {SegmentId} for conversation {ConversationId} turn {Turn}",
                    segment.SegmentId, segment.ConversationId, segment.TurnNumber);
            else
                _logger.LogWarning(
                    "Failed to persist episodic segment for conversation {ConversationId} turn {Turn}: {Errors}",
                    segment.ConversationId, segment.TurnNumber, string.Join(", ", result.Errors));
        }
        catch (Exception ex)
        {
            // Isolate the segment write so a failure here does not mask a successful episode capture.
            _logger.LogWarning(ex,
                "Episodic-segment capture failed for conversation {ConversationId} turn {Turn}",
                segment.ConversationId, segment.TurnNumber);
        }
    }
}
