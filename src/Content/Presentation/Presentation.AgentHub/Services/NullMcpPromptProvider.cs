using Application.AI.Common.Interfaces;
using Domain.AI.MCP;

namespace Presentation.AgentHub.Services;

/// <summary>
/// Null-object implementation of <see cref="IMcpPromptProvider"/>.
/// Registered via <c>TryAddSingleton</c> as the default — real implementations
/// registered later (e.g. in an Infrastructure layer) will take precedence.
/// Enables direct constructor injection without service-locator patterns for this optional dependency.
/// </summary>
internal sealed class NullMcpPromptProvider : IMcpPromptProvider
{
    /// <inheritdoc />
    public Task<IReadOnlyList<McpPrompt>> GetPromptsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<McpPrompt>>([]);
}
