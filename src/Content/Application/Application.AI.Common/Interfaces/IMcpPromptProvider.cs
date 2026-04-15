using Domain.AI.MCP;

namespace Application.AI.Common.Interfaces;

/// <summary>
/// Provides MCP prompt templates for discovery via the HTTP API.
/// This interface is optional — consumers should resolve it via
/// <c>IServiceProvider.GetService&lt;IMcpPromptProvider&gt;()</c> and handle a <see langword="null"/>
/// result gracefully by returning an empty collection.
/// </summary>
public interface IMcpPromptProvider
{
    /// <summary>Returns all registered prompt templates.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<McpPrompt>> GetPromptsAsync(CancellationToken ct = default);
}
