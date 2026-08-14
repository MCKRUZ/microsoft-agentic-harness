using Application.AI.Common.Exceptions;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Skills;
using Application.Common.Helpers;
using Domain.AI.Egress;
using Domain.AI.Skills;
using Domain.AI.Tools;
using Domain.Common.Config.AI;
using Infrastructure.AI.Governance;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Skills;

/// <summary>
/// Parses custom frontmatter fields from a raw SKILL.md file path into a <see cref="SkillDefinition"/>.
/// </summary>
/// <remarks>
/// As of <c>Microsoft.Agents.AI</c> 1.13.0, the framework's <c>AgentFileSkillsSource</c> promotes
/// only <c>name</c>, <c>description</c>, <c>license</c>, <c>compatibility</c>, and
/// <c>allowed-tools</c>, and <em>silently discards</em> every other top-level frontmatter key —
/// there is no fallback branch and no warning. Its <c>metadata:</c> escape hatch captures flat
/// string values only, so it cannot represent the harness's structured <c>tools</c> (list of
/// objects) or <c>egress</c> (nested map) fields. Re-check these observations on an SDK bump.
/// <para>
/// This parser therefore owns the harness-specific frontmatter; see <c>ParseFromFile</c> below
/// for the authoritative list. Background: <c>docs/plans/skills-refactor-to-framework.md</c>.
/// </para>
/// <para>
/// That list includes the structured <c>tools:</c> block, which the framework cannot represent
/// at all: its <c>metadata:</c> escape hatch captures flat strings, while a tool declaration is
/// a list of maps carrying operations, an optional flag, and a fallback. Parsing it here is what
/// makes <see cref="SkillDefinition.ToolDeclarations"/> reach the runtime (issue #222).
/// </para>
/// </remarks>
public sealed partial class SkillMetadataParser
{
    private readonly ILogger<SkillMetadataParser> _logger;
    private readonly ISkillFileReader _fileReader;
    private readonly IMcpSecurityScanner _scanner;
    private readonly IOptionsMonitor<AIConfig> _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="SkillMetadataParser"/> class.
    /// </summary>
    /// <param name="logger">Logger for parse diagnostics.</param>
    /// <param name="fileReader">
    /// Sandboxed, read-only access to skill content. Every manifest read goes through it so a
    /// <c>SKILL.md</c> outside the configured skill roots cannot be loaded (issue #247).
    /// </param>
    /// <param name="scanner">
    /// Screens a skill's name, description, and instructions for prompt-injection payloads before
    /// <see cref="Build"/> constructs the <see cref="SkillDefinition"/> — a plugin-sourced skill is
    /// third-party content by definition (issue #331).
    /// </param>
    /// <param name="config">
    /// Supplies the scanning policy (<see cref="GovernanceConfig.EnableMcpSecurity"/>,
    /// <see cref="GovernanceConfig.McpToolBlockThreshold"/>); read per call so a config reload takes
    /// effect, matching <c>ScanningMcpToolProvider</c>'s convention for the same policy.
    /// </param>
    public SkillMetadataParser(
        ILogger<SkillMetadataParser> logger,
        ISkillFileReader fileReader,
        IMcpSecurityScanner scanner,
        IOptionsMonitor<AIConfig> config)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(fileReader);
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(config);

        _logger = logger;
        _fileReader = fileReader;
        _scanner = scanner;
        _config = config;
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
        var raw = _fileReader.ReadText(skillFilePath);
        var (rawFrontmatter, body) = ExtractFrontmatterAndBody(raw);

        var frontmatter = SkillFrontmatter.Load(rawFrontmatter);

        return Build(
            frontmatter,
            body,
            fallbackName: Path.GetFileName(sourcePath),
            explicitDescription: null,
            skillFilePath,
            sourcePath,
            pluginSource);
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
            if (_fileReader.FileExists(skillFilePath))
            {
                var raw = _fileReader.ReadText(skillFilePath);
                rawFrontmatter = ExtractFrontmatterAndBody(raw).Frontmatter;
            }
        }
        catch (Exception ex) when (ex is not SkillPathRefusedException)
        {
            // A sandbox refusal is deliberately NOT caught here. Degrading to null frontmatter looks
            // harmless but is not: SkillFrontmatter.Load(null) yields a skill with an EMPTY
            // allowed-tools list and no egress policy, which downstream is indistinguishable from a
            // manifest that legitimately declares neither. A skill whose manifest the sandbox
            // refuses must fail loudly rather than load with its restrictions quietly dropped.
            _logger.LogWarning(ex, "Could not read custom frontmatter from {Path}", skillFilePath);
        }

        var frontmatter = SkillFrontmatter.Load(rawFrontmatter);

        return Build(
            frontmatter,
            body,
            fallbackName: skillName,
            explicitDescription: skillDescription ?? string.Empty,
            skillFilePath,
            resolvedSourcePath,
            pluginSource);
    }

    /// <summary>
    /// Maps parsed frontmatter and a markdown body onto a <see cref="SkillDefinition"/>. Shared by
    /// both entry points so the two cannot drift apart on which fields they promote — a drift that
    /// previously left one overload silently missing a field the other had.
    /// </summary>
    private SkillDefinition Build(
        SkillFrontmatter frontmatter,
        string body,
        string fallbackName,
        string? explicitDescription,
        string skillFilePath,
        string baseDirectory,
        string? pluginSource)
    {
        var (objectives, traceFormat, instructions) = ExtractStructuredSections(body);

        var name = explicitDescription is null
            ? frontmatter.String("name") ?? fallbackName
            : fallbackName;
        var description = explicitDescription ?? frontmatter.String("description") ?? string.Empty;
        var toolDeclarations = frontmatter.ToolDeclarations();

        ScanOrRefuse(name, description, body, toolDeclarations, skillFilePath);

        var metaBlock = frontmatter.ScalarBlock("metadata");

        return new SkillDefinition
        {
            Id = name,
            Name = name,
            Description = description,
            Instructions = instructions,
            Objectives = objectives,
            TraceFormat = traceFormat,
            Category = frontmatter.String("category"),
            SkillType = frontmatter.String("skill_type"),
            Version = frontmatter.String("version"),
            ModelOverride = frontmatter.String("model-override"),
            AgentId = frontmatter.String("agent-id"),
            Tags = frontmatter.StringList("tags"),
            AllowedTools = frontmatter.StringList("allowed-tools"),
            ToolDeclarations = toolDeclarations,
            Prerequisites = frontmatter.StringList("prerequisites"),
            CompletionTool = frontmatter.String("completion_tool"),
            Metadata = metaBlock?.ToDictionary(kv => kv.Key, kv => (object)kv.Value),
            Author = metaBlock != null && metaBlock.TryGetValue("author", out var author) ? author : null,
            FilePath = skillFilePath,
            BaseDirectory = baseDirectory,
            LoadedAt = DateTime.UtcNow,

            PluginSource = pluginSource,
            Egress = frontmatter.Egress(),
        };
    }

    /// <summary>
    /// Screens a skill's name/description, its full markdown body, and its declared tools'
    /// human-readable guidance for prompt-injection payloads before <see cref="Build"/> constructs
    /// the <see cref="SkillDefinition"/> — a plugin-sourced skill is third-party content by
    /// definition, screened the same way an MCP tool description already is (issue #331). No
    /// exemption for first-party skills shipped in this template: "it came from our own directory"
    /// is exactly the assumption an attacker with file-write access defeats for free.
    /// </summary>
    /// <remarks>
    /// Name/description are short fields, scanned with the full rule set (same shape as a tool
    /// description). The body is scanned whole — not the post-<see cref="StripSections"/>
    /// <c>instructions</c> value — because <c>## Objectives</c> and <c>## Trace Format</c> are
    /// stripped out of <c>instructions</c> for a different reason (they're surfaced to callers as
    /// separate fields, not concatenated into the instructions the agent reads) and are just as
    /// agent-facing as the rest of the body; scanning only the stripped remainder would leave those
    /// two sections as an unscreened injection channel. Tool-declaration guidance
    /// (<c>Description</c>/<c>WhenToUse</c>/<c>WhenNotToUse</c>) is prose of unbounded length, same
    /// as the body, and is joined onto it into one scan — both already run with the length-sensitive
    /// rules excluded (a multi-thousand-token manifest routinely contains a legitimate 40+ character
    /// token — a hash, a UUID — that the base64-block rule cannot distinguish from an encoded
    /// payload), so combining them costs nothing and halves the long-form scan count.
    /// </remarks>
    private void ScanOrRefuse(
        string name, string description, string body, IList<ToolDeclaration>? toolDeclarations, string skillFilePath)
    {
        var toolGuidance = toolDeclarations is { Count: > 0 }
            ? string.Join(
                '\n',
                toolDeclarations
                    .SelectMany(t => new[] { t.Description, t.WhenToUse, t.WhenNotToUse })
                    .Where(s => !string.IsNullOrWhiteSpace(s)))
            : null;
        var longForm = string.IsNullOrWhiteSpace(toolGuidance) ? body : $"{body}\n{toolGuidance}";

        ManifestSecurityGate.ScanOrRefuse(
            _scanner, _logger, _config.CurrentValue.Governance, name, "skill", skillFilePath,
            shortFieldsContent: $"{name}\n{description}",
            longForm);
    }

    /// <remarks>
    /// Delegates the delimiter search to <see cref="YamlFrontmatterHelper"/>, which requires the
    /// closing <c>---</c> to occupy its own line. A previous hand-rolled version of this method
    /// searched with <c>raw.IndexOf("---", 3)</c>, which matches <c>---</c> anywhere — including
    /// inside a YAML value such as <c>description: "compare A --- B"</c> — silently truncating the
    /// frontmatter and leaking the remaining keys into the body.
    /// <para>
    /// Line endings are normalised to LF for the benefit of readers downstream of this method, not
    /// of the YAML parse: YamlDotNet handles CRLF correctly on its own (checked). It is kept
    /// because the returned text is also what gets logged and compared, and mixed endings there
    /// are a nuisance rather than a defect. Every SKILL.md in this repository is CRLF on disk.
    /// </para>
    /// </remarks>
    private static (string? Frontmatter, string Body) ExtractFrontmatterAndBody(string raw)
    {
        var (yaml, body) = YamlFrontmatterHelper.ExtractFrontmatter(raw);

        var frontmatter = string.IsNullOrEmpty(yaml)
            ? null
            : yaml.Replace("\r\n", "\n", StringComparison.Ordinal);

        return (frontmatter, body.Trim());
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
}
