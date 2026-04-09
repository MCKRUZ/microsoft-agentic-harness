using Domain.AI.Config;

namespace Application.AI.Common.Interfaces.Config;

/// <summary>
/// Discovers configuration files by walking the directory tree upward from
/// the current working directory. Supports @include directives and
/// frontmatter-based path scoping.
/// </summary>
public interface IConfigDiscoveryService
{
    /// <summary>
    /// Discovers all configuration files starting from the specified directory
    /// and walking upward to the filesystem root.
    /// </summary>
    /// <param name="startDirectory">The directory to start discovery from (typically CWD).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Discovered files ordered by priority (highest priority first).</returns>
    Task<IReadOnlyList<DiscoveredConfigFile>> DiscoverAsync(
        string startDirectory,
        CancellationToken cancellationToken = default);
}
