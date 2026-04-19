using Application.Common.Helpers;
using Domain.AI.Agents;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Agents;

/// <summary>
/// Parses the YAML frontmatter of an <c>AGENT.md</c> file into an <see cref="AgentDefinition"/>.
/// </summary>
/// <remarks>
/// Intentionally limited to the Level 1 metadata tier required by
/// <see cref="Application.AI.Common.Interfaces.IAgentMetadataRegistry"/>: identity, categorisation,
/// tags, and source paths. Tool declarations, state configuration, and decision frameworks live
/// on the richer <see cref="AgentManifest"/> and are parsed separately by whichever component
/// actually executes the agent.
/// </remarks>
public sealed class AgentMetadataParser
{
    private readonly ILogger<AgentMetadataParser> _logger;

    /// <summary>Initialises the parser with a logger for malformed-frontmatter diagnostics.</summary>
    public AgentMetadataParser(ILogger<AgentMetadataParser> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Reads and parses an <c>AGENT.md</c> file from disk.
    /// </summary>
    /// <param name="agentFilePath">Absolute path to the <c>AGENT.md</c> file.</param>
    /// <param name="baseDirectory">Directory containing the <c>AGENT.md</c>; used as the <see cref="AgentDefinition.BaseDirectory"/>.</param>
    /// <returns>An <see cref="AgentDefinition"/> populated from the frontmatter.</returns>
    public AgentDefinition ParseFromFile(string agentFilePath, string baseDirectory)
    {
        var raw = File.ReadAllText(agentFilePath);
        var (yaml, _) = YamlFrontmatterHelper.ExtractFrontmatter(raw);

        if (string.IsNullOrWhiteSpace(yaml))
            _logger.LogWarning("AGENT.md at {Path} has no YAML frontmatter; falling back to folder-name identity", agentFilePath);

        var name = ParseString(yaml, "name") ?? Path.GetFileName(baseDirectory);
        var id = ParseString(yaml, "id") ?? name;

        return new AgentDefinition
        {
            Id = id,
            Name = name,
            Description = ParseString(yaml, "description") ?? string.Empty,
            Category = ParseString(yaml, "category"),
            Domain = ParseString(yaml, "domain"),
            Version = ParseString(yaml, "version"),
            Author = ParseString(yaml, "author"),
            Tags = ParseList(yaml, "tags"),
            Skill = ParseString(yaml, "skill"),
            FilePath = agentFilePath,
            BaseDirectory = baseDirectory,
            LoadedAt = DateTime.UtcNow,
        };
    }

    private static string? ParseString(string? frontmatter, string key)
    {
        if (string.IsNullOrEmpty(frontmatter))
            return null;

        foreach (var line in frontmatter.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = trimmed[(key.Length + 1)..].Trim().Trim('"', '\'');
            return string.IsNullOrEmpty(value) ? null : value;
        }

        return null;
    }

    private static IReadOnlyList<string> ParseList(string? frontmatter, string key)
    {
        if (string.IsNullOrEmpty(frontmatter))
            return [];

        foreach (var line in frontmatter.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))
                continue;

            var rest = trimmed[(key.Length + 1)..].Trim();
            if (!rest.StartsWith('['))
                return [];

            return rest.Trim('[', ']')
                .Split(',')
                .Select(s => s.Trim().Trim('"', '\''))
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();
        }

        return [];
    }
}
