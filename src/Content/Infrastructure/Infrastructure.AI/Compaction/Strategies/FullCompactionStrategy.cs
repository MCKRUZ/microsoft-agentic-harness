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
/// Full compaction strategy that sends the entire conversation history to the LLM
/// for summarization. Most thorough approach but incurs an API call cost.
/// </summary>
public sealed class FullCompactionStrategy : ICompactionStrategyExecutor
{
    private const string SummarizationPrompt =
        "Summarize the following conversation, preserving key decisions, code changes, " +
        "file paths, and action items. Be concise.";

    private readonly IChatClientFactory _chatClientFactory;
    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly ILogger<FullCompactionStrategy> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="FullCompactionStrategy"/>.
    /// </summary>
    /// <param name="chatClientFactory">Factory for creating LLM chat clients.</param>
    /// <param name="options">Application configuration for model deployment settings.</param>
    /// <param name="logger">Logger for compaction operations.</param>
    public FullCompactionStrategy(
        IChatClientFactory chatClientFactory,
        IOptionsMonitor<AppConfig> options,
        ILogger<FullCompactionStrategy> logger)
    {
        _chatClientFactory = chatClientFactory;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public CompactionStrategy Strategy => CompactionStrategy.Full;

    /// <inheritdoc />
    public async Task<CompactionResult> ExecuteAsync(
        string agentId,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        try
        {
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

            foreach (var message in messages)
            {
                summarizationMessages.Add(message);
            }

            var response = await chatClient.GetResponseAsync(
                summarizationMessages,
                cancellationToken: cancellationToken);

            var summary = response.Text ?? string.Empty;
            var postTokens = TokenEstimationHelper.EstimateTokens(summary);

            var boundary = new CompactionBoundaryMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                Trigger = CompactionTrigger.Manual,
                Strategy = CompactionStrategy.Full,
                PreCompactionTokens = preTokens,
                PostCompactionTokens = postTokens,
                Timestamp = DateTimeOffset.UtcNow,
                Summary = summary
            };

            _logger.LogDebug(
                "Full compaction completed for agent {AgentId}: {PreTokens} -> {PostTokens} tokens",
                agentId, preTokens, postTokens);

            return CompactionResult.Succeeded(boundary);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Full compaction failed for agent {AgentId}", agentId);
            return CompactionResult.Failed($"LLM summarization failed: {ex.Message}");
        }
    }

}
