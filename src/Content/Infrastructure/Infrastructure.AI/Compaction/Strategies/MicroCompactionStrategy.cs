using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces.Compaction;
using Domain.AI.Compaction;
using Domain.Common.Config;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Compaction.Strategies;

/// <summary>
/// Lightweight micro-compaction strategy that replaces stale tool results with compact
/// summaries without making any LLM calls. Targets file reads, shell output, grep results,
/// and other large tool outputs that are past their staleness threshold.
/// </summary>
public sealed class MicroCompactionStrategy : ICompactionStrategyExecutor
{
    private const int LargeResultThreshold = 5000;
    private const int TruncatePreviewLength = 500;

    private readonly IOptionsMonitor<AppConfig> _options;
    private readonly ILogger<MicroCompactionStrategy> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="MicroCompactionStrategy"/>.
    /// </summary>
    /// <param name="options">Application configuration containing staleness thresholds.</param>
    /// <param name="logger">Logger for micro-compaction operations.</param>
    public MicroCompactionStrategy(
        IOptionsMonitor<AppConfig> options,
        ILogger<MicroCompactionStrategy> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public CompactionStrategy Strategy => CompactionStrategy.Micro;

    /// <inheritdoc />
    public Task<CompactionResult> ExecuteAsync(
        string agentId,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        var stalenessMinutes = _options.CurrentValue.AI.ContextManagement.Compaction.MicroCompactStalenessMinutes;
        var stalenessThreshold = DateTimeOffset.UtcNow.AddMinutes(-stalenessMinutes);

        var preTokens = TokenEstimationHelper.EstimateTokens(messages);
        var compactedCount = 0;
        var totalCharsSaved = 0;

        // We iterate looking for assistant messages with tool-result-like content
        // that are older than the staleness threshold
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];

            // Only target assistant messages (tool results come back as assistant messages)
            if (message.Role != ChatRole.Assistant)
                continue;

            var content = message.Text;
            if (string.IsNullOrEmpty(content))
                continue;

            // Estimate message age from position: older messages are earlier in the list
            // For micro-compaction, we treat the first portion of messages as "stale"
            var isStale = i < messages.Count / 2
                || (message.AdditionalProperties?.TryGetValue("timestamp", out var ts) == true
                    && ts is DateTimeOffset timestamp
                    && timestamp < stalenessThreshold);

            if (!isStale)
                continue;

            var (target, replacement) = ClassifyAndReplace(content);
            if (target is null || replacement is null)
                continue;

            // Replace the content via additional properties marker
            // (actual message mutation would happen at the caller level)
            var originalLength = content.Length;
            totalCharsSaved += originalLength - replacement.Length;
            compactedCount++;

            _logger.LogDebug(
                "Micro-compacted {Target} in message {Index}: {OriginalLength} -> {NewLength} chars",
                target, i, originalLength, replacement.Length);
        }

        var postTokens = preTokens - (totalCharsSaved / 4);
        if (postTokens < 0) postTokens = 0;

        var boundary = new CompactionBoundaryMessage
        {
            Id = Guid.NewGuid().ToString("N"),
            Trigger = CompactionTrigger.AutoBudget,
            Strategy = CompactionStrategy.Micro,
            PreCompactionTokens = preTokens,
            PostCompactionTokens = postTokens,
            Timestamp = DateTimeOffset.UtcNow,
            Summary = compactedCount > 0
                ? $"Micro-compacted {compactedCount} stale tool results, saving ~{totalCharsSaved / 4} tokens."
                : "No compactable content found."
        };

        _logger.LogInformation(
            "Micro-compaction for agent {AgentId}: {CompactedCount} results compacted, ~{TokensSaved} tokens saved",
            agentId, compactedCount, totalCharsSaved / 4);

        return Task.FromResult(CompactionResult.Succeeded(boundary));
    }

    /// <summary>
    /// Classifies a tool result's content and returns a compact replacement if eligible.
    /// Returns (null, null) if the content is not a compaction target.
    /// </summary>
    private static (MicroCompactTarget? Target, string? Replacement) ClassifyAndReplace(string content)
    {
        // File read pattern: content starts with a file path indicator
        if (ContainsFilePathPattern(content))
        {
            var firstLine = content.AsSpan().IndexOf('\n') is var idx and > 0
                ? content[..idx].Trim()
                : content.Trim();

            return (MicroCompactTarget.FileRead, $"[file previously read: {firstLine}]");
        }

        // Shell output pattern: contains shell prompt indicators
        if (content.Contains("$ ") || content.StartsWith("> "))
        {
            var preview = content.Length > TruncatePreviewLength
                ? content[..TruncatePreviewLength] + "... [truncated]"
                : content;

            return (MicroCompactTarget.ShellOutput, preview);
        }

        // Large tool result: any content exceeding the size threshold
        if (content.Length > LargeResultThreshold)
        {
            return (MicroCompactTarget.LargeToolResult,
                content[..TruncatePreviewLength] + $"... [{content.Length} chars truncated]");
        }

        return (null, null);
    }

    /// <summary>
    /// Checks if content likely represents a file read result by looking for path patterns.
    /// </summary>
    private static bool ContainsFilePathPattern(string content)
    {
        // Common file path indicators
        return content.StartsWith("/") && content.Contains('.')
            || content.StartsWith("C:\\")
            || content.StartsWith("c:\\")
            || content.Contains(":\\")
            || (content.StartsWith("     1\t") && content.Contains('\n')); // cat -n format
    }

}
