using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.MediatR;
using Application.AI.Common.Interfaces.Routing;
using Application.AI.Common.Interfaces.Skills;
using Domain.AI.Routing.Models;
using Domain.Common.Config;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.MediatRBehaviors;

/// <summary>
/// Post-turn pipeline behavior that records, per skill, whether this turn succeeded — the write half
/// of the procedural-memory loop (#695). The two-part system this drives — recorded outcomes here,
/// consulted amendments in <c>AgentExecutionContextFactory</c> — existed and was fully tested before
/// this behavior did, but had zero production callers; this is what turns it on.
/// </summary>
/// <remarks>
/// <para>
/// Only activates for requests implementing <see cref="IAgentTurnRequest"/> that produce an
/// <see cref="IAgentTurnResult"/> whose <see cref="IAgentTurnResult.SkillIds"/> is non-empty. Both
/// success and failure are recorded — a failed turn is itself a signal worth learning from. A Magentic
/// turn's <c>SkillIds</c> is always empty (see <c>MagenticAgentTurnRunner</c>'s remarks on why a
/// whole-workflow outcome cannot be flattened onto every participant's skills), so it correctly records
/// nothing here rather than attributing noise.
/// </para>
/// <para>
/// "What kind of query was this" comes from <see cref="IRequestIntentClassifier"/> (#699) — the same
/// classification key <see cref="ISkillEffectivenessTracker.GetEffectivenessAsync"/> is later queried
/// with, so a write here and a future read line up on the same key.
/// </para>
/// <para>
/// Runs as fire-and-forget on a background thread, mirroring <see cref="WorkEpisodeCaptureBehavior{TRequest, TResponse}"/>:
/// the agent's response returns immediately, and the closure captures no request-scoped service and not
/// the request <see cref="System.Threading.CancellationToken"/> — both are gone once the turn returns. It
/// injects <see cref="IServiceScopeFactory"/>, creates a fresh DI scope, and re-establishes it as the
/// ambient request scope so tenant/owner-aware stores resolve identity from a live provider. Classification
/// and every recorded outcome are isolated so one skill's failure never masks another's, or the turn's own
/// response.
/// </para>
/// </remarks>
public sealed class SkillEffectivenessTrackingBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAmbientRequestScope _ambientScope;
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly ILogger<SkillEffectivenessTrackingBehavior<TRequest, TResponse>> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SkillEffectivenessTrackingBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    public SkillEffectivenessTrackingBehavior(
        IServiceScopeFactory scopeFactory,
        IAmbientRequestScope ambientScope,
        IOptionsMonitor<AppConfig> appConfig,
        ILogger<SkillEffectivenessTrackingBehavior<TRequest, TResponse>> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(ambientScope);
        ArgumentNullException.ThrowIfNull(appConfig);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _ambientScope = ambientScope;
        _appConfig = appConfig;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var response = await next();

        // Read live so this gate alone stays hot-reloadable — but flipping it true at runtime is not,
        // by itself, sufficient to enable the feature: ISkillEffectivenessTracker is only registered
        // when Infrastructure.AI.KnowledgeGraph's DI extension observes this same flag at STARTUP.
        // Enabling it live still passes this check and then fails GetRequiredService<ISkillEffectivenessTracker>()
        // in RecordAsync below, which the outer catch there logs and swallows once per turn rather than
        // ever recording an outcome — fail-open by design (same as an absent knowledge-graph host
        // entirely), but worth knowing before assuming a runtime flip alone turns this on.
        if (!_appConfig.CurrentValue.AI.Rag.GraphRag.SkillEffectivenessEnabled)
            return response;

        if (request is not IAgentTurnRequest agentRequest || response is not IAgentTurnResult turnResult)
            return response;

        if (turnResult.SkillIds.Count == 0)
            return response;

        // Snapshot the values the background task needs so the closure captures no request-scoped
        // service and not the request CancellationToken — both are gone once the turn returns.
        var conversationId = agentRequest.ConversationId;
        var turnNumber = agentRequest.TurnNumber;
        var userMessage = agentRequest.UserMessage;
        var skillIds = turnResult.SkillIds;
        var succeeded = turnResult.Success;

        _ = Task.Run(() => RecordAsync(conversationId, turnNumber, userMessage, skillIds, succeeded));

        return response;
    }

    /// <summary>
    /// Classifies the turn's query kind, then records an outcome per skill in a single fresh DI scope.
    /// Failures are logged and swallowed — effectiveness tracking is an enhancement, never a hard
    /// dependency of a turn. A failure recording one skill does not abort the others.
    /// </summary>
    private async Task RecordAsync(
        string conversationId,
        int turnNumber,
        string userMessage,
        IReadOnlyList<string> skillIds,
        bool succeeded)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();

            // Re-establish the fresh, alive scope as the ambient request scope so scope-aware
            // dependencies resolve identity from a live provider (mirrors WorkEpisodeCaptureBehavior).
            using var _ = _ambientScope.BeginScope(scope.ServiceProvider);

            var classifier = scope.ServiceProvider.GetRequiredService<IRequestIntentClassifier>();
            var tracker = scope.ServiceProvider.GetRequiredService<ISkillEffectivenessTracker>();

            var assessment = await classifier.ClassifyAsync(
                new AgentTurnContext
                {
                    ConversationId = conversationId,
                    UserMessage = userMessage,
                    TurnNumber = turnNumber,
                },
                CancellationToken.None);
            var queryClassification = assessment.Intent.ToString();

            await Task.WhenAll(skillIds.Select(skillId =>
                RecordOutcomeAsync(tracker, skillId, queryClassification, succeeded, conversationId, turnNumber)));
        }
        catch (Exception ex)
        {
            // Backstop: BeginScope/CreateAsyncScope/classification failures, or anything the per-skill
            // catch missed. This runs on a discarded Task.Run, so an unobserved exception would
            // otherwise escape.
            _logger.LogWarning(ex,
                "Skill-effectiveness tracking failed for conversation {ConversationId} turn {Turn}",
                conversationId, turnNumber);
        }
    }

    private async Task RecordOutcomeAsync(
        ISkillEffectivenessTracker tracker,
        string skillId,
        string queryClassification,
        bool succeeded,
        string conversationId,
        int turnNumber)
    {
        try
        {
            await tracker.RecordOutcomeAsync(skillId, queryClassification, succeeded, cancellationToken: CancellationToken.None);

            _logger.LogDebug(
                "Recorded skill effectiveness for {SkillId} ({Classification}, {Outcome}) — conversation {ConversationId} turn {Turn}",
                skillId, queryClassification, succeeded ? "success" : "failure", conversationId, turnNumber);
        }
        catch (Exception ex)
        {
            // Isolate this skill's write so one skill's failure never masks another's.
            _logger.LogWarning(ex,
                "Failed to record effectiveness for skill {SkillId}, conversation {ConversationId} turn {Turn}",
                skillId, conversationId, turnNumber);
        }
    }
}
