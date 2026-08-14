using Application.AI.Common.Interfaces.Governance;
using Application.Common.Helpers;
using Domain.AI.Agents;
using Domain.Common.Config.AI;
using Infrastructure.AI.Governance;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Agents;

/// <summary>
/// Parses the YAML frontmatter of an <c>AGENT.md</c> file into an <see cref="AgentDefinition"/>.
/// </summary>
/// <remarks>
/// Parses the fields an <see cref="AgentDefinition"/> carries: identity, categorisation, tags, and
/// source paths, plus the agent's own instructions (the <c>AGENT.md</c> body), its tool ceiling
/// (<c>allowed-tools</c>), and the ids of the skills it composes. Per-turn work such as resolving those
/// skills, merging instructions, and provisioning tools is done by the agent factory at build time, not here.
/// </remarks>
public sealed class AgentMetadataParser
{
    private readonly ILogger<AgentMetadataParser> _logger;
    private readonly IMcpSecurityScanner _scanner;
    private readonly IOptionsMonitor<AIConfig> _config;

    /// <summary>
    /// Initialises the parser with a logger for malformed-frontmatter diagnostics, and a scanner
    /// that screens the manifest for prompt-injection payloads before it is trusted (issue #331).
    /// </summary>
    public AgentMetadataParser(
        ILogger<AgentMetadataParser> logger,
        IMcpSecurityScanner scanner,
        IOptionsMonitor<AIConfig> config)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(config);

        _logger = logger;
        _scanner = scanner;
        _config = config;
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
        var (yaml, body) = YamlFrontmatterHelper.ExtractFrontmatter(raw);

        if (string.IsNullOrWhiteSpace(yaml))
            _logger.LogWarning("AGENT.md at {Path} has no YAML frontmatter; falling back to folder-name identity", agentFilePath);

        var name = ParseString(yaml, "name") ?? Path.GetFileName(baseDirectory);
        var id = ParseString(yaml, "id") ?? name;
        var description = ParseString(yaml, "description") ?? string.Empty;
        // Only trust the body as instructions when frontmatter actually parsed. When `yaml` is
        // empty the frontmatter was absent or malformed (already warned above), and ExtractFrontmatter
        // returns the whole file as `body` — capturing that would leak the raw `---`/YAML lines into
        // the agent's system prompt.
        var instructions = string.IsNullOrWhiteSpace(yaml) || string.IsNullOrWhiteSpace(body) ? null : body.Trim();

        ScanOrRefuse(name, description, instructions, agentFilePath);

        return new AgentDefinition
        {
            Id = id,
            Name = name,
            Description = description,
            Category = ParseString(yaml, "category"),
            Domain = ParseString(yaml, "domain"),
            Version = ParseString(yaml, "version"),
            Author = ParseString(yaml, "author"),
            Tags = ParseList(yaml, "tags"),
            Skills = ParseSkills(yaml),
            AllowedTools = ParseList(yaml, "allowed-tools"),
            Instructions = instructions,
            FilePath = agentFilePath,
            BaseDirectory = baseDirectory,
            LoadedAt = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Screens an agent manifest's name/description and, separately, its instructions body for
    /// prompt-injection payloads before <see cref="ParseFromFile"/> constructs the
    /// <see cref="AgentDefinition"/> (issue #331). No exemption for first-party agents shipped in
    /// this template — see <see cref="Infrastructure.AI.Skills.SkillMetadataParser"/>'s sibling
    /// method for the same reasoning.
    /// </summary>
    /// <remarks>
    /// Unlike the skill-loading path, a refusal here is <b>not</b> automatically skip-and-continue
    /// for a bundle: <c>BundleStagingService</c>'s AGENT.md parse sits inside a plain
    /// <c>catch (Exception ex)</c> that fails the whole bundle — an agent manifest is the bundle's
    /// identity, so refusing the entire bundle when it's poisoned is the intended behaviour, not a
    /// gap. Discovery-path callers (<c>AgentMetadataRegistry.DiscoverInDirectory</c>) already catch
    /// generically and skip that one agent, continuing with the rest.
    /// </remarks>
    private void ScanOrRefuse(string name, string description, string? instructions, string agentFilePath) =>
        ManifestSecurityGate.ScanOrRefuse(
            _scanner, _logger, _config.CurrentValue.Governance, name, "agent", agentFilePath,
            shortFieldsContent: $"{name}\n{description}",
            instructions);

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

    /// <summary>
    /// Parses skill IDs from frontmatter, trying the plural <c>skills:</c> key first,
    /// then falling back to the singular <c>skill:</c> key for backward compatibility.
    /// </summary>
    private static IReadOnlyList<string> ParseSkills(string? frontmatter)
    {
        var list = ParseList(frontmatter, "skills");
        if (list.Count > 0)
            return list;

        var single = ParseString(frontmatter, "skill");
        return single is not null ? [single] : [];
    }
}
