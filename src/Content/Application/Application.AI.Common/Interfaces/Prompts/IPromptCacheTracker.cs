using Domain.AI.Prompts;
using Microsoft.Extensions.AI;

namespace Application.AI.Common.Interfaces.Prompts;

/// <summary>
/// Tracks prompt content hashes between turns to detect cache breaks.
/// Useful for debugging cost increases and optimizing prompt stability.
/// </summary>
public interface IPromptCacheTracker
{
    /// <summary>
    /// Takes a hash snapshot of the current system prompt and tool schemas.
    /// </summary>
    /// <param name="systemPrompt">The full system prompt text to hash.</param>
    /// <param name="tools">The tool list whose schemas will be hashed individually and combined.</param>
    /// <returns>A snapshot containing the system hash, combined tools hash, and per-tool hashes.</returns>
    PromptHashSnapshot TakeSnapshot(string systemPrompt, IReadOnlyList<AITool> tools);

    /// <summary>
    /// Compares two snapshots and returns a report of what changed.
    /// Returns null if there are no changes.
    /// </summary>
    /// <param name="previous">The earlier snapshot to compare against.</param>
    /// <param name="current">The newer snapshot to compare.</param>
    /// <returns>A report detailing what changed, or null if the snapshots are identical.</returns>
    PromptCacheBreakReport? Compare(PromptHashSnapshot previous, PromptHashSnapshot current);
}
