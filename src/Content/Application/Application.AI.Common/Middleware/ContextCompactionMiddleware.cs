using System.Runtime.CompilerServices;
using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces.Compaction;
using Domain.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Middleware;

/// <summary>
/// Chat client middleware that compacts conversation history before it is sent to the model
/// when the estimated token footprint exceeds the configured budget. Runs on every live turn,
/// re-evaluating the incoming history so that growth mid-conversation (including tool-call
/// rounds) is trimmed before the next model call.
/// </summary>
/// <remarks>
/// <para>
/// Positioned in the chat client pipeline alongside the other per-call concerns (after
/// <see cref="ToolDiagnosticsMiddleware"/>, before the distributed cache). Each call estimates the
/// token count of the incoming messages and, when
/// <see cref="IContextCompactionService.ShouldAutoCompact"/> reports the budget is exceeded,
/// delegates to <see cref="IContextCompactionService.CompactAsync"/> to produce a summary of the
/// prior history. The middleware then rebuilds the outgoing message list as
/// <c>[system summary] + [current turn]</c> so the model retains the immediate request while the
/// older context collapses into the summary.
/// </para>
/// <para>
/// Compaction is <b>fail-open</b>: when the compaction service returns a failure (for example the
/// LLM summarizer is unavailable or the circuit breaker is open), the original, untrimmed history
/// is forwarded unchanged. A compaction problem must never break a live turn.
/// </para>
/// <para>
/// Wired only when <c>AppConfig.AI.ContextManagement.Compaction.MiddlewareEnabled</c> is
/// <see langword="true"/> and an <see cref="IContextCompactionService"/> is registered; otherwise
/// <see cref="Factories.AgentFactory"/> leaves it out of the pipeline entirely, preserving the
/// default (no-compaction) behaviour.
/// </para>
/// </remarks>
public sealed class ContextCompactionMiddleware : DelegatingChatClient
{
    private readonly IContextCompactionService _service;
    private readonly string _agentId;
    private readonly int _maxContextTokens;
    private readonly CompactionStrategy _strategy;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContextCompactionMiddleware"/> class.
    /// </summary>
    /// <param name="innerClient">The inner chat client to wrap.</param>
    /// <param name="service">The compaction service that decides and performs compaction.</param>
    /// <param name="agentId">Identifier of the agent, used to scope circuit-breaker state.</param>
    /// <param name="maxContextTokens">
    /// The maximum context-window token budget. Compaction triggers once the estimated history
    /// tokens reach this value scaled by the configured auto-compact threshold ratio.
    /// </param>
    /// <param name="strategy">The compaction strategy to apply when the budget is exceeded.</param>
    /// <param name="logger">Logger for compaction diagnostics.</param>
    public ContextCompactionMiddleware(
        IChatClient innerClient,
        IContextCompactionService service,
        string agentId,
        int maxContextTokens,
        CompactionStrategy strategy,
        ILogger<ContextCompactionMiddleware> logger)
        : base(innerClient)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
        _agentId = agentId;
        _maxContextTokens = maxContextTokens;
        _strategy = strategy;
        _logger = logger;
    }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var effective = await CompactIfNeededAsync(messages, cancellationToken);
        return await base.GetResponseAsync(effective, options, cancellationToken);
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var effective = await CompactIfNeededAsync(messages, cancellationToken);

        await foreach (var update in base.GetStreamingResponseAsync(effective, options, cancellationToken))
        {
            yield return update;
        }
    }

    /// <summary>
    /// Compacts the supplied history when it exceeds the configured budget, returning the trimmed
    /// message list. Returns the original messages unchanged when compaction is not needed or fails.
    /// </summary>
    private async Task<IReadOnlyList<ChatMessage>> CompactIfNeededAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        var history = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();

        var currentTokens = TokenEstimationHelper.EstimateTokens(history);
        if (!_service.ShouldAutoCompact(_agentId, currentTokens, _maxContextTokens))
            return history;

        _logger.LogInformation(
            "[Compaction] Agent {AgentId} history ~{Tokens} tokens exceeds budget {Budget}; applying {Strategy} compaction",
            _agentId, currentTokens, _maxContextTokens, _strategy);

        var result = await _service.CompactAsync(_agentId, history, _strategy, cancellationToken);

        if (!result.Success || result.Boundary is null)
        {
            _logger.LogWarning(
                "[Compaction] Compaction did not apply for agent {AgentId} ({Error}); forwarding full history",
                _agentId, result.Error ?? "no boundary produced");
            return history;
        }

        var rebuilt = RebuildHistory(history, result.Boundary.Summary);
        _logger.LogInformation(
            "[Compaction] Agent {AgentId} history compacted: {Before} -> {After} messages (~{Saved} tokens saved)",
            _agentId, history.Count, rebuilt.Count, result.Boundary.TokensSaved);
        return rebuilt;
    }

    /// <summary>
    /// Rebuilds the outgoing history as a single summary system message followed by the current
    /// turn (every message from the last user message onward). When no user message is present the
    /// final message is preserved so the model still has an actionable prompt.
    /// </summary>
    private static IReadOnlyList<ChatMessage> RebuildHistory(
        IReadOnlyList<ChatMessage> history,
        string summary)
    {
        var lastUserIndex = -1;
        for (var i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Role == ChatRole.User)
            {
                lastUserIndex = i;
                break;
            }
        }

        var tailStart = lastUserIndex >= 0
            ? lastUserIndex
            : Math.Max(0, history.Count - 1);

        var rebuilt = new List<ChatMessage>(history.Count - tailStart + 1)
        {
            new(ChatRole.System, summary)
        };

        for (var i = tailStart; i < history.Count; i++)
            rebuilt.Add(history[i]);

        return rebuilt;
    }
}
