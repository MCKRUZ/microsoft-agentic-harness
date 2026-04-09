using Domain.AI.Prompts;

namespace Application.AI.Common.Interfaces.Prompts;

/// <summary>
/// Provides a specific section of the system prompt. Multiple providers can be registered,
/// each responsible for one section type. Providers are resolved via DI as an
/// <c>IEnumerable&lt;IPromptSectionProvider&gt;</c> collection.
/// </summary>
public interface IPromptSectionProvider
{
    /// <summary>Gets the section type this provider supplies.</summary>
    SystemPromptSectionType SectionType { get; }

    /// <summary>
    /// Computes the section content for the specified agent.
    /// Called by the composer — may be cached if the section is cacheable.
    /// </summary>
    /// <param name="agentId">The agent to compute the section for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The computed section, or <c>null</c> if no content is available.</returns>
    Task<SystemPromptSection?> GetSectionAsync(
        string agentId,
        CancellationToken cancellationToken = default);
}
