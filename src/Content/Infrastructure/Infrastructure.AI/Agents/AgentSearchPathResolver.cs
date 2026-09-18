using Domain.Common.Config.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Agents;

/// <summary>
/// Resolves <see cref="AgentsConfig.AllPaths"/> into the absolute, existing directories agent
/// discovery should actually scan.
/// </summary>
/// <remarks>
/// Extracted from <c>AgentMetadataRegistry.Discover</c> (issue #705) so
/// <see cref="BackgroundServices.AgentManifestWatcherService"/> computes the same set of
/// watch roots the registry scans, rather than two independent implementations of "which
/// folders" silently drifting apart.
/// </remarks>
internal static class AgentSearchPathResolver
{
    /// <summary>
    /// Resolves <paramref name="agentsConfig"/>'s configured paths against
    /// <see cref="AppContext.BaseDirectory"/> (relative paths are resolved from the bin folder so
    /// they match where csproj Content Include copies agents at build time, rather than coupling to
    /// the process CWD, which differs between <c>dotnet run</c> and a published deployment) and
    /// filters to directories that actually exist. A configured path that does not exist is logged
    /// and skipped, never treated as an error — a template consumer's unused
    /// <c>AdditionalPaths</c> entry must not break discovery of everything else.
    /// </summary>
    /// <param name="agentsConfig">The configured agent search paths, or <see langword="null"/>.</param>
    /// <param name="logger">Logger for path-resolution diagnostics.</param>
    /// <returns>The absolute, existing directories to scan. Empty when none are configured or none exist.</returns>
    public static IReadOnlyList<string> Resolve(AgentsConfig? agentsConfig, ILogger logger)
    {
        var paths = agentsConfig?.AllPaths.ToList() ?? [];
        if (paths.Count == 0)
        {
            logger.LogInformation("No agent paths configured in AppConfig.AI.Agents — skipping agent discovery");
            return [];
        }

        var resolved = new List<string>();
        foreach (var p in paths)
        {
            string abs;
            try
            {
                abs = Path.IsPathRooted(p) ? p : Path.GetFullPath(p, AppContext.BaseDirectory);
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
            {
                // A malformed AdditionalPaths entry must not abort resolution of every other
                // configured path (issue #705 code review) — the same "skip and log, don't fail
                // the host" contract every other entry in this loop already gets.
                logger.LogWarning(ex, "Agent path is malformed, skipping: {Path}", p);
                continue;
            }

            if (Directory.Exists(abs))
                resolved.Add(abs);
            else
                logger.LogWarning("Agent path not found, skipping: {Path}", abs);
        }

        if (resolved.Count == 0)
            logger.LogWarning("No valid agent paths found — agent discovery produced no results");

        return resolved;
    }
}
