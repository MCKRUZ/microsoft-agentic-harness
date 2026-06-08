using Domain.AI.Egress;
using Domain.AI.Skills;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Skills;

/// <summary>
/// Parses custom frontmatter fields from a raw SKILL.md file path into a <see cref="SkillDefinition"/>.
/// </summary>
/// <remarks>
/// The framework's <c>FileAgentSkillLoader</c> parses only the standard <c>name</c> and
/// <c>description</c> fields. This parser extracts harness-specific fields:
/// <c>category</c>, <c>tags</c>, <c>version</c>, <c>model-override</c>, <c>agent-id</c>,
/// <c>allowed-tools</c>, <c>prerequisites</c>, <c>completion_tool</c>, and <c>skill_type</c>.
/// </remarks>
public sealed class SkillMetadataParser
{
    private readonly ILogger<SkillMetadataParser> _logger;

    public SkillMetadataParser(ILogger<SkillMetadataParser> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Parses a SKILL.md file from disk into a <see cref="SkillDefinition"/>.
    /// Extracts both standard fields (name, description) and harness-specific frontmatter.
    /// </summary>
    /// <param name="skillFilePath">Absolute path to the SKILL.md file.</param>
    /// <param name="sourcePath">Directory containing the SKILL.md file (used as <c>BaseDirectory</c>).</param>
    /// <param name="pluginSource">Optional plugin source identifier; set when loading skills from a plugin package.</param>
    public SkillDefinition ParseFromFile(string skillFilePath, string sourcePath, string? pluginSource = null)
    {
        var raw = File.ReadAllText(skillFilePath);
        var frontmatter = ExtractFrontmatter(raw);
        var body = ExtractBody(raw, frontmatter);

        var (objectives, traceFormat, instructions) = ExtractStructuredSections(body);

        var name = ParseString(frontmatter, "name") ?? Path.GetFileName(sourcePath);
        var description = ParseString(frontmatter, "description") ?? string.Empty;

        var metaBlock = ParseNestedBlock(frontmatter, "metadata");

        return new SkillDefinition
        {
            Id = name,
            Name = name,
            Description = description,
            Instructions = instructions,
            Objectives = objectives,
            TraceFormat = traceFormat,
            Category = ParseString(frontmatter, "category"),
            SkillType = ParseString(frontmatter, "skill_type"),
            Version = ParseString(frontmatter, "version"),
            ModelOverride = ParseString(frontmatter, "model-override"),
            AgentId = ParseString(frontmatter, "agent-id"),
            Tags = ParseList(frontmatter, "tags"),
            AllowedTools = ParseList(frontmatter, "allowed-tools"),
            Prerequisites = ParseList(frontmatter, "prerequisites"),
            CompletionTool = ParseString(frontmatter, "completion_tool"),
            Metadata = metaBlock?.ToDictionary(kv => kv.Key, kv => (object)kv.Value),
            Author = metaBlock != null && metaBlock.TryGetValue("author", out var author) ? author : null,
            FilePath = skillFilePath,
            BaseDirectory = sourcePath,
            LoadedAt = DateTime.UtcNow,

            PluginSource = pluginSource,
            Egress = ParseEgressManifest(frontmatter),
        };
    }

    /// <summary>
    /// Builds a <see cref="SkillDefinition"/> from pre-parsed field values (e.g., from the framework's loader).
    /// </summary>
    /// <param name="skillName">The skill name.</param>
    /// <param name="skillDescription">The skill description.</param>
    /// <param name="body">The SKILL.md body content (after frontmatter).</param>
    /// <param name="sourcePath">Directory containing the SKILL.md file.</param>
    /// <param name="pluginSource">Optional plugin source identifier; set when loading skills from a plugin package.</param>
    public SkillDefinition Parse(string skillName, string? skillDescription, string body, string sourcePath, string? pluginSource = null)
    {
        // Resolve to a canonical absolute path to eliminate any traversal sequences (e.g. "../")
        // before constructing the file path from caller-supplied input.
        var resolvedSourcePath = Path.GetFullPath(sourcePath);
        var skillFilePath = Path.Combine(resolvedSourcePath, "SKILL.md");
        string? rawFrontmatter = null;

        try
        {
            if (File.Exists(skillFilePath))
            {
                var raw = File.ReadAllText(skillFilePath);
                rawFrontmatter = ExtractFrontmatter(raw);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read custom frontmatter from {Path}", skillFilePath);
        }

        var (objectives, traceFormat, instructions) = ExtractStructuredSections(body);

        var metaBlock = ParseNestedBlock(rawFrontmatter, "metadata");

        return new SkillDefinition
        {
            Id = skillName,
            Name = skillName,
            Description = skillDescription ?? string.Empty,
            Instructions = instructions,
            Objectives = objectives,
            TraceFormat = traceFormat,
            Category = ParseString(rawFrontmatter, "category"),
            SkillType = ParseString(rawFrontmatter, "skill_type"),
            Version = ParseString(rawFrontmatter, "version"),
            ModelOverride = ParseString(rawFrontmatter, "model-override"),
            AgentId = ParseString(rawFrontmatter, "agent-id"),
            Tags = ParseList(rawFrontmatter, "tags"),
            AllowedTools = ParseList(rawFrontmatter, "allowed-tools"),
            Prerequisites = ParseList(rawFrontmatter, "prerequisites"),
            CompletionTool = ParseString(rawFrontmatter, "completion_tool"),
            Metadata = metaBlock?.ToDictionary(kv => kv.Key, kv => (object)kv.Value),
            Author = metaBlock != null && metaBlock.TryGetValue("author", out var author) ? author : null,
            FilePath = skillFilePath,
            BaseDirectory = resolvedSourcePath,
            LoadedAt = DateTime.UtcNow,

            PluginSource = pluginSource,
            Egress = ParseEgressManifest(rawFrontmatter),
        };
    }

    private static string? ExtractFrontmatter(string raw)
    {
        if (!raw.StartsWith("---", StringComparison.Ordinal))
            return null;

        var end = raw.IndexOf("---", 3, StringComparison.Ordinal);
        return end < 0 ? null : raw[3..end];
    }

    private static string ExtractBody(string raw, string? frontmatter)
    {
        if (frontmatter == null)
            return raw.Trim();

        // Skip the opening ---, frontmatter block, and closing ---
        var closingDelimiter = raw.IndexOf("---", 3, StringComparison.Ordinal);
        if (closingDelimiter < 0)
            return raw.Trim();

        var bodyStart = closingDelimiter + 3;
        return bodyStart >= raw.Length ? string.Empty : raw[bodyStart..].Trim();
    }

    /// <summary>
    /// Extracts Objectives, TraceFormat, and stripped Instructions from a skill body in one pass.
    /// </summary>
    private static (string? Objectives, string? TraceFormat, string Instructions) ExtractStructuredSections(string body)
    {
        return (
            ExtractSection(body, "Objectives"),
            ExtractSection(body, "Trace Format"),
            StripSections(body, "Objectives", "Trace Format")
        );
    }

    /// <summary>
    /// Extracts the content of a named ## Heading section from a markdown body.
    /// Returns null if the heading is not present. Content ends at the next ## heading or EOF.
    /// Matching is case-insensitive; headings inside code fences are ignored.
    /// </summary>
    private static string? ExtractSection(string body, string heading)
    {
        var lines = body.Split('\n');
        var searchHeading = $"## {heading}";

        var startIdx = -1;
        var inFence = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
                inFence = !inFence;

            if (!inFence && trimmed.Equals(searchHeading, StringComparison.OrdinalIgnoreCase))
            {
                startIdx = i;
                break;
            }
        }

        if (startIdx < 0)
            return null;

        var endIdx = lines.Length;
        inFence = false;
        for (var i = startIdx + 1; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
                inFence = !inFence;

            if (!inFence && lines[i].TrimStart().StartsWith("## ", StringComparison.Ordinal))
            {
                endIdx = i;
                break;
            }
        }

        var content = string.Join('\n', lines[(startIdx + 1)..endIdx]).Trim();
        return string.IsNullOrWhiteSpace(content) ? null : content;
    }

    /// <summary>
    /// Returns the body with the specified ## Heading sections removed.
    /// Consecutive blank lines left by removal are collapsed to at most one.
    /// Headings inside code fences are not treated as section boundaries.
    /// </summary>
    private static string StripSections(string body, params string[] headings)
    {
        var headingSet = new HashSet<string>(
            headings.Select(h => $"## {h}"),
            StringComparer.OrdinalIgnoreCase);

        var lines = body.Split('\n');
        var result = new List<string>(lines.Length);
        var skipping = false;
        var inFence = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
                inFence = !inFence;

            if (!inFence && headingSet.Contains(trimmed))
            {
                skipping = true;
                continue;
            }

            if (!inFence && skipping && line.TrimStart().StartsWith("## ", StringComparison.Ordinal))
                skipping = false;

            if (!skipping)
                result.Add(line);
        }

        // Collapse runs of blank lines to at most one
        var normalized = new List<string>(result.Count);
        var blankRun = 0;
        foreach (var line in result)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                blankRun++;
                if (blankRun <= 1)
                    normalized.Add(line);
            }
            else
            {
                blankRun = 0;
                normalized.Add(line);
            }
        }

        return string.Join('\n', normalized).Trim();
    }

    private static string? ParseString(string? frontmatter, string key)
    {
        if (string.IsNullOrEmpty(frontmatter))
            return null;

        foreach (var line in frontmatter.Split('\n'))
        {
            // Skip indented lines — they belong to a nested block, not the top level
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
                continue;

            var trimmed = line.Trim();
            if (!trimmed.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = trimmed[(key.Length + 1)..].Trim().Trim('"', '\'');
            return string.IsNullOrEmpty(value) ? null : value;
        }

        return null;
    }

    private static IList<string> ParseList(string? frontmatter, string key)
    {
        if (string.IsNullOrEmpty(frontmatter))
            return [];

        foreach (var line in frontmatter.Split('\n'))
        {
            // Skip indented lines — they belong to a nested block, not the top level
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
                continue;

            var trimmed = line.Trim();
            if (!trimmed.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))
                continue;

            // Inline YAML array: ["a", "b"] or [a, b]
            var rest = trimmed[(key.Length + 1)..].Trim();
            if (rest.StartsWith('['))
            {
                return rest.Trim('[', ']')
                    .Split(',')
                    .Select(s => s.Trim().Trim('"', '\''))
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();
            }
        }

        return [];
    }

    /// <summary>
    /// Extracts all indented key-value pairs under a parent key (e.g., <c>metadata:</c>).
    /// Returns null if the block is not found or contains no entries.
    /// The block ends at the first non-indented line following the parent key.
    /// </summary>
    private static Dictionary<string, string>? ParseNestedBlock(string? frontmatter, string parentKey)
    {
        if (string.IsNullOrEmpty(frontmatter))
            return null;

        var lines = frontmatter.Split('\n');
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inBlock = false;

        foreach (var line in lines)
        {
            if (!inBlock)
            {
                // Match unindented "parentKey:" with no value after the colon
                var trimmed = line.Trim();
                if (trimmed.Equals(parentKey + ":", StringComparison.OrdinalIgnoreCase))
                    inBlock = true;

                continue;
            }

            // A non-indented line (or empty) terminates the block
            if (line.Length == 0 || (line[0] != ' ' && line[0] != '\t'))
                break;

            // Parse "  key: value" — split on first colon only
            var stripped = line.Trim();
            var colonIdx = stripped.IndexOf(':', StringComparison.Ordinal);
            if (colonIdx <= 0)
                continue;

            var entryKey = stripped[..colonIdx].Trim();
            var entryValue = stripped[(colonIdx + 1)..].Trim().Trim('"', '\'');

            if (!string.IsNullOrEmpty(entryKey))
                result[entryKey] = entryValue;
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// Parses the <c>egress.allowlist</c> nested block from YAML frontmatter.
    /// Returns null when the <c>egress</c> key is absent. Returns an empty
    /// manifest when the key is present but the allowlist is empty. Each list
    /// item is a flat map with keys <c>host</c>, <c>hostPattern</c>,
    /// <c>schemes</c>, <c>ports</c>. Unknown keys inside an entry are silently
    /// ignored so future fields don't break older parsers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The parser is deliberately tolerant of indentation widths (2 or 4 spaces),
    /// inline arrays (<c>[a, b]</c>) and quoted strings. It is NOT tolerant of
    /// the manifest's SEMANTIC rules (leftmost-label wildcards, http/https
    /// schemes, valid ports) — those are enforced by
    /// <c>EgressManifestValidator</c> in the Application layer. The parser's
    /// only job is to map YAML onto the domain shape; whether the shape is
    /// valid is a separate concern.
    /// </para>
    /// <para>
    /// Hand-rolled rather than YamlDotNet because the broader frontmatter parser
    /// is hand-rolled too (matches existing style) and adding a YAML dependency
    /// to Infrastructure.AI just to read one nested block isn't warranted.
    /// </para>
    /// </remarks>
    internal static EgressManifest? ParseEgressManifest(string? frontmatter)
    {
        if (string.IsNullOrEmpty(frontmatter))
            return null;

        var lines = frontmatter.Split('\n');

        // Find "egress:" at the top level.
        var egressIdx = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
                continue;

            if (line.Trim().Equals("egress:", StringComparison.OrdinalIgnoreCase))
            {
                egressIdx = i;
                break;
            }
        }

        if (egressIdx < 0)
            return null;

        // Find "allowlist:" nested under egress (first indented line that equals "allowlist:").
        var allowlistIdx = -1;
        for (var i = egressIdx + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0 || (line[0] != ' ' && line[0] != '\t'))
                break; // egress block ended

            if (line.Trim().Equals("allowlist:", StringComparison.OrdinalIgnoreCase))
            {
                allowlistIdx = i;
                break;
            }
        }

        if (allowlistIdx < 0)
        {
            // egress: declared but no allowlist — treat as empty manifest.
            return new EgressManifest { Allowlist = [] };
        }

        var entries = ParseAllowlistItems(lines, allowlistIdx);
        return new EgressManifest { Allowlist = entries };
    }

    private static IReadOnlyList<EgressAllowlistEntry> ParseAllowlistItems(string[] lines, int allowlistIdx)
    {
        // Establish base indentation of list items by looking at the first line that begins with '-'.
        var entries = new List<EgressAllowlistEntry>();
        var i = allowlistIdx + 1;

        // Determine item-indent depth from the first '- ' line under allowlist.
        var itemIndent = -1;
        for (var probe = i; probe < lines.Length; probe++)
        {
            var raw = lines[probe];
            if (raw.Length == 0)
                continue;

            var leading = CountLeadingSpaces(raw);
            if (leading == 0)
                return entries; // out of the egress block entirely

            var trimmed = raw.TrimStart();
            if (trimmed.StartsWith('-'))
            {
                itemIndent = leading;
                break;
            }

            // A non-list indented line means allowlist value is malformed — stop.
            return entries;
        }

        if (itemIndent < 0)
            return entries;

        while (i < lines.Length)
        {
            var raw = lines[i];
            if (raw.Length == 0)
            {
                i++;
                continue;
            }

            var leading = CountLeadingSpaces(raw);

            // Exit when indentation falls back to a shallower level than item-indent.
            if (leading < itemIndent)
                break;

            var trimmed = raw.TrimStart();

            if (leading == itemIndent && trimmed.StartsWith('-'))
            {
                // Start of a new entry — collect lines until the next item or block exit.
                var (entry, consumed) = ReadOneEntry(lines, i, itemIndent);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
                i += consumed;
            }
            else
            {
                // Unexpected line shape — skip defensively.
                i++;
            }
        }

        return entries;
    }

    private static (EgressAllowlistEntry? Entry, int Consumed) ReadOneEntry(string[] lines, int startIdx, int itemIndent)
    {
        // The first line is "  - key: value" or just "  -".
        string? host = null;
        string? hostPattern = null;
        IReadOnlyList<string> schemes = [];
        IReadOnlyList<int> ports = [];

        var first = lines[startIdx].TrimStart();
        // Strip the leading '-' and any space following it.
        var firstAfterDash = first.Length > 1 ? first[1..].TrimStart() : string.Empty;
        if (!string.IsNullOrEmpty(firstAfterDash))
        {
            ApplyEntryKvp(firstAfterDash, ref host, ref hostPattern, ref schemes, ref ports);
        }

        var i = startIdx + 1;
        while (i < lines.Length)
        {
            var raw = lines[i];
            if (raw.Length == 0)
            {
                i++;
                continue;
            }

            var leading = CountLeadingSpaces(raw);
            var trimmed = raw.TrimStart();

            // End of this entry when we see the next '-' at item-indent or anything shallower.
            if (leading <= itemIndent && trimmed.StartsWith('-'))
                break;

            if (leading <= itemIndent)
                break;

            // Continuation line inside the entry — parse "key: value".
            ApplyEntryKvp(trimmed, ref host, ref hostPattern, ref schemes, ref ports);
            i++;
        }

        // If nothing parsed, skip the entry.
        if (host is null && hostPattern is null && schemes.Count == 0 && ports.Count == 0)
            return (null, i - startIdx);

        var entry = new EgressAllowlistEntry
        {
            Host = host,
            HostPattern = hostPattern,
            Schemes = schemes,
            Ports = ports
        };

        return (entry, i - startIdx);
    }

    private static void ApplyEntryKvp(
        string trimmed,
        ref string? host,
        ref string? hostPattern,
        ref IReadOnlyList<string> schemes,
        ref IReadOnlyList<int> ports)
    {
        var colon = trimmed.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
            return;

        var key = trimmed[..colon].Trim();
        var value = trimmed[(colon + 1)..].Trim();

        if (key.Equals("host", StringComparison.OrdinalIgnoreCase))
            host = string.IsNullOrEmpty(value) ? null : value.Trim('"', '\'');
        else if (key.Equals("hostPattern", StringComparison.OrdinalIgnoreCase))
            hostPattern = string.IsNullOrEmpty(value) ? null : value.Trim('"', '\'');
        else if (key.Equals("schemes", StringComparison.OrdinalIgnoreCase))
            schemes = ParseInlineStringArray(value);
        else if (key.Equals("ports", StringComparison.OrdinalIgnoreCase))
            ports = ParseInlineIntArray(value);
        // Unknown keys silently ignored for forward compatibility.
    }

    private static IReadOnlyList<string> ParseInlineStringArray(string raw)
    {
        if (string.IsNullOrEmpty(raw) || !raw.StartsWith('['))
            return [];

        return raw.Trim('[', ']')
            .Split(',')
            .Select(s => s.Trim().Trim('"', '\''))
            .Where(s => !string.IsNullOrEmpty(s))
            .ToArray();
    }

    private static IReadOnlyList<int> ParseInlineIntArray(string raw)
    {
        if (string.IsNullOrEmpty(raw) || !raw.StartsWith('['))
            return [];

        var result = new List<int>();
        foreach (var token in raw.Trim('[', ']').Split(','))
        {
            var t = token.Trim().Trim('"', '\'');
            if (int.TryParse(t, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var port))
                result.Add(port);
        }
        return result;
    }

    private static int CountLeadingSpaces(string line)
    {
        var count = 0;
        foreach (var ch in line)
        {
            if (ch == ' ')
                count++;
            else if (ch == '\t')
                count += 4; // treat a tab as 4 spaces for indent counting
            else
                break;
        }
        return count;
    }
}
