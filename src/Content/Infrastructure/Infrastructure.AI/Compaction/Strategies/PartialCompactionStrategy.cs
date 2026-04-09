using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Compaction;
using Domain.AI.Compaction;
using Domain.Common.Config;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Compaction.Strategies;

/// <summary>
/// Partial compaction strategy that summarizes only the first half of the conversation
/// history while preserving the most recent messages intact. Balances context reduction
/// with recency preservation.
/// </summary>
public sealed class PartialCompactionStrategy : ICompactionStrategyExecutor
{
    private const string SummarizationPrompt =
        "Summarize the following conversation excerpt, preserving key decisions, code changes, " +
        "file paths, and action items. Be concise. This is the older portion of a longer conversation.";

    private readonly IChatClientFactory _chatClientFactory;
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly ILogger<PartialCompactionStrategy> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="PartialCompactionStrategy"/>.
    /// </summary>
    /// <param name="chatClientFactory">Factory for creating LLM chat clients.</param>
    /// <param name="options">Application configuration for model deployment settings.</param>
    /// <param name="logger">Logger for compaction operations.</param>
    public PartialCompactionStrategy(
        IChatClientFactory chatClientFactory,
        IOptionsMonitor<AppConfig> options,
        ILogger<PartialCompactionStrategy> logger)
    {
        _chatClientFactory = chatClientFactory;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public CompactionStrategy Strategy => CompactionStrategy.Partial;

    /// <inheritdoc />
    public async Task<CompactionResult> ExecuteAsync(
        string agentId,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (messages.Count < 2)
            {
                return CompactionResult.Failed("Not enough messages for partial compaction.");
            }

            var pivotIndex = messages.Count / 2;
            var olderMessages = messages.Take(pivotIndex).ToList();
            var preservedMessages = messages.Skip(pivotIndex).ToList();

            var preTokens = TokenEstimationHelper.EstimateTokens(messages);

            var config = _options.CurrentValue.AI.AgentFramework;
            var chatClient = await _chatClientFactory.GetChatClientAsync(
                config.ClientType,
                config.DefaultDeployment,
                cancellationToken);

            var summarizationMessages = new List<ChatMessage>
            {
                new(ChatRole.System, SummarizationPrompt)
            };

            foreach (var message in olderMessages)
            {
                summarizationMessages.Add(message);
            }

            var response = await chatClient.GetResponseAsync(
                summarizationMessages,
                cancellationToken: cancellationToken);

            var summary = response.Text ?? string.Empty;
            var postTokens = TokenEstimationHelper.EstimateTokens(summary) + TokenEstimationHelper.EstimateTokens(preservedMessages);

            var boundary = new CompactionBoundaryMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                Trigger = CompactionTrigger.Manual,
                Strategy = CompactionStrategy.Partial,
                PreCompactionTokens = preTokens,
                PostCompactionTokens = postTokens,
                Timestamp = DateTimeOffset.UtcNow,
                Summary = summary
            };

            _logger.LogDebug(
                "Partial compaction completed for agent {AgentId}: {PreTokens} -> {PostTokens} tokens (pivot at message {Pivot})",
                agentId, preTokens, postTokens, pivotIndex);

            return CompactionResult.Succeeded(boundary);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Partial compaction failed for agent {AgentId}", agentId);
            return CompactionResult.Failed($"LLM summarization failed: {ex.Message}");
        }
    }

}
