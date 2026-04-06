using Application.Common.Interfaces.Common;
using Domain.AI.Enums;

namespace Application.AI.Common.Interfaces.Common;

/// <summary>
/// Extends <see cref="IDirectoryMapper"/> with AI-specific directory resolution
/// for agent manifests, skill definitions, and MCP server state.
/// </summary>
/// <remarks>
/// Registered as singleton in DI alongside <see cref="IDirectoryMapper"/>.
/// Infrastructure implements both interfaces on the same class.
/// </remarks>
public interface IAgentDirectoryMapper : IDirectoryMapper
{
    /// <summary>
    /// Gets the absolute path for an agent-specific directory.
    /// </summary>
    /// <param name="directory">The agent directory type to resolve.</param>
    /// <returns>Absolute path guaranteed to exist.</returns>
    string GetAbsolutePath(AgentDirectory directory);
}
