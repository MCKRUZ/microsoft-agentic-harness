using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.KnowledgeGraph;
using Application.AI.Common.Interfaces.MediatR;
using Domain.Common.Config.AI;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.MediatRBehaviors;

/// <summary>
/// Post-turn pipeline behavior that extracts notable facts from agent conversations
/// and persists them to the knowledge graph via <see cref="IKnowledgeMemory"/>.
/// </summary>
/// <remarks>
/// <para>
/// Only activates for requests implementing <see cref="IAgentTurnRequest"/> that produce
/// a successful <see cref="IAgentTurnResult"/> with a non-empty response.
/// All other request types pass through untouched.
/// </para>
/// <para>
/// Extraction runs as fire-and-forget on a background thread. The agent's response
/// is returned immediately — extraction failures are logged but never propagate.
/// </para>
/// <para>
/// Because the background task outlives the MediatR request scope, it must not capture
/// request-scoped services (<see cref="IConversationFactExtractor"/>, <see cref="IKnowledgeMemory"/>)
/// or the request <see cref="System.Threading.CancellationToken"/>. Instead it injects
/// <see cref="IServiceScopeFactory"/> and creates a fresh DI scope for the post-turn write —
/// mirroring the established pattern in <c>RetentionEnforcementService</c>. The fresh scope is
/// also re-established as the ambient request scope for the duration of the write so that
/// tenant/compliance-aware stores resolve their identity from an <em>alive</em> provider rather
/// than the disposed request scope captured in the execution context.
/// </para>
/// </remarks>
public sealed class KnowledgeExtractionBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAmbientRequestScope _ambientScope;
    private readonly KnowledgeBridgeConfig _config;
    private readonly ILogger<KnowledgeExtractionBehavior<TRequest, TResponse>> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="KnowledgeExtractionBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    public KnowledgeExtractionBehavior(
        IServiceScopeFactory scopeFactory,
        IAmbientRequestScope ambientScope,
        IOptions<KnowledgeBridgeConfig> config,
        ILogger<KnowledgeExtractionBehavior<TRequest, TResponse>> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(ambientScope);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _ambientScope = ambientScope;
        _config = config.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var response = await next();

        if (!_config.Enabled)
            return response;

        if (request is not IAgentTurnRequest agentRequest ||
            response is not IAgentTurnResult { Success: true, Response.Length: > 0 } turnResult)
            return response;

        // Snapshot the values the background task needs so the closure captures no
        // request-scoped service and not the request CancellationToken. Both would be
        // disposed/cancelled once the agent turn returns.
        var userMessage = agentRequest.UserMessage;
        var assistantResponse = turnResult.Response;
        var conversationId = agentRequest.ConversationId;
        var turnNumber = agentRequest.TurnNumber;

        _ = Task.Run(() => ExtractAndPersistAsync(
            userMessage, assistantResponse, conversationId, turnNumber));

        return response;
    }

    /// <summary>
    /// Runs the post-turn extraction-and-persist in a fresh DI scope, bounded only by the
    /// configured extraction timeout (never by the request token, which is already gone).
    /// </summary>
    private async Task ExtractAndPersistAsync(
        string userMessage,
        string assistantResponse,
        string conversationId,
        int turnNumber)
    {
        try
        {
            using var cts = new CancellationTokenSource(
                TimeSpan.FromSeconds(_config.ExtractionTimeoutSeconds));

            await using var scope = _scopeFactory.CreateAsyncScope();

            // Re-establish the fresh, alive scope as the ambient request scope so tenant/
            // compliance-aware stores resolve their identity from a live provider rather than
            // the disposed request scope still pinned in the captured execution context.
            using var _ = _ambientScope.BeginScope(scope.ServiceProvider);

            var extractor = scope.ServiceProvider.GetRequiredService<IConversationFactExtractor>();
            var knowledgeMemory = scope.ServiceProvider.GetRequiredService<IKnowledgeMemory>();

            var facts = await extractor.ExtractAsync(
                userMessage,
                assistantResponse,
                conversationId,
                turnNumber,
                cts.Token);

            foreach (var fact in facts)
            {
                try
                {
                    await knowledgeMemory.RememberAsync(
                        fact.Key, fact.Content, fact.EntityType, cts.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex,
                        "Failed to persist fact {Key} for conversation {ConversationId}",
                        fact.Key, conversationId);
                }
            }

            if (facts.Count > 0)
            {
                _logger.LogInformation(
                    "Persisted {Count} facts from conversation {ConversationId} turn {Turn}",
                    facts.Count, conversationId, turnNumber);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Knowledge extraction failed for conversation {ConversationId} turn {Turn}",
                conversationId, turnNumber);
        }
    }
}
