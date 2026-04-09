using System.Security.Cryptography;
using System.Text;
using Application.AI.Common.Interfaces.Prompts;
using Domain.AI.Prompts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Prompts;

/// <summary>
/// SHA256-based implementation of <see cref="IPromptCacheTracker"/>.
/// Hashes system prompts and tool schemas to detect changes between turns.
/// </summary>
public sealed class Sha256PromptCacheTracker : IPromptCacheTracker
{
    private readonly ILogger<Sha256PromptCacheTracker> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="Sha256PromptCacheTracker"/>.
    /// </summary>
    /// <param name="logger">Logger for diagnostic output when cache breaks are detected.</param>
    public Sha256PromptCacheTracker(ILogger<Sha256PromptCacheTracker> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public PromptHashSnapshot TakeSnapshot(string systemPrompt, IReadOnlyList<AITool> tools)
    {
        var systemHash = HashString(systemPrompt);

        var perToolHashes = new Dictionary<string, string>(tools.Count);
        foreach (var tool in tools)
        {
            var toolContent = $"{tool.Name}:{tool.Description}";
            perToolHashes[tool.Name] = HashString(toolContent);
        }

        var combinedToolContent = string.Join("|", perToolHashes.OrderBy(kv => kv.Key).Select(kv => kv.Value));
        var toolsHash = HashString(combinedToolContent);

        return new PromptHashSnapshot
        {
            SystemHash = systemHash,
            ToolsHash = toolsHash,
            PerToolHashes = perToolHashes,
            Timestamp = DateTimeOffset.UtcNow
        };
    }

    /// <inheritdoc />
    public PromptCacheBreakReport? Compare(PromptHashSnapshot previous, PromptHashSnapshot current)
    {
        var systemChanged = !string.Equals(previous.SystemHash, current.SystemHash, StringComparison.Ordinal);
        var toolsChanged = !string.Equals(previous.ToolsHash, current.ToolsHash, StringComparison.Ordinal);

        if (!systemChanged && !toolsChanged)
        {
            return null;
        }

        var changedTools = new List<string>();

        if (toolsChanged)
        {
            var allToolNames = previous.PerToolHashes.Keys
                .Union(current.PerToolHashes.Keys)
                .Distinct(StringComparer.Ordinal);

            foreach (var toolName in allToolNames)
            {
                var hadPrevious = previous.PerToolHashes.TryGetValue(toolName, out var prevHash);
                var hasCurrent = current.PerToolHashes.TryGetValue(toolName, out var currHash);

                if (!hadPrevious || !hasCurrent || !string.Equals(prevHash, currHash, StringComparison.Ordinal))
                {
                    changedTools.Add(toolName);
                }
            }

            _logger.LogDebug(
                "Prompt cache break detected: SystemChanged={SystemChanged}, ToolsChanged={ToolsChanged}, ChangedTools=[{ChangedTools}]",
                systemChanged,
                toolsChanged,
                string.Join(", ", changedTools));
        }
        else if (systemChanged)
        {
            _logger.LogDebug("Prompt cache break detected: system prompt changed");
        }

        return new PromptCacheBreakReport
        {
            SystemChanged = systemChanged,
            ToolsChanged = toolsChanged,
            ChangedToolNames = changedTools,
            Previous = previous,
            Current = current
        };
    }

    private static string HashString(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hashBytes);
    }
}
