using Domain.AI.Prompts;

namespace Application.AI.Common.Interfaces.Prompts;

/// <summary>
/// Composes the full system prompt from registered section providers.
/// Supports memoization, independent section invalidation, and budget-aware assembly.
/// </summary>
public interface ISystemPromptComposer
{
    /// <summary>
    /// Composes the full system prompt for the specified agent within the given token budget.
    /// Sections are resolved from providers, cached when cacheable, sorted by priority,
    /// and concatenated. Low-priority sections are dropped if the budget is exceeded.
    /// </summary>
    /// <param name="agentId">The agent to compose the prompt for.</param>
    /// <param name="tokenBudget">Maximum token budget for the assembled prompt.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The assembled system prompt string.</returns>
    Task<string> ComposeAsync(
        string agentId,
        int tokenBudget,
        CancellationToken cancellationToken = default);

    /// <summary>Invalidates all cached sections of the specified type.</summary>
    /// <param name="type">The section type to invalidate.</param>
    void InvalidateSection(SystemPromptSectionType type);

    /// <summary>Invalidates all cached sections for all types.</summary>
    void InvalidateAll();
}
