using System.Text.RegularExpressions;
using Domain.AI.Skills;
using Domain.Common.Helpers;
using Microsoft.Agents.AI;

namespace Application.AI.Common.Helpers;

/// <summary>
/// Decides which skills the framework's file-backed skills source will actually surface, so the harness
/// knows whose instructions it may safely leave out of the static system prompt and defer to the
/// <c>load_skill</c> tool.
/// </summary>
/// <remarks>
/// <para>
/// This is the gate on progressive disclosure. Omitting a skill body from the prompt is only correct when
/// something else will supply it on demand; omit one the framework never loaded and the agent silently
/// runs with no instructions — no exception, no empty-section error, just a worse answer. So the
/// predicate below is deliberately <em>conservative</em>: every uncertainty, including an unreadable or
/// unparseable file, resolves to <see langword="false"/>, and a <see langword="false"/> result means
/// "keep injecting the body eagerly". The failure direction is always redundancy, never silence.
/// </para>
/// <para>
/// <b>The frontmatter is re-read here rather than taken from <see cref="SkillDefinition"/>, and that is
/// deliberate.</b> <c>SkillMetadataParser</c> defaults a missing <c>name</c> to the directory name and a
/// missing <c>description</c> to the empty string, so a <see cref="SkillDefinition"/> cannot distinguish
/// "declared in frontmatter" from "supplied by our parser". The framework requires both to be present and
/// valid <em>in the file</em> and rejects the skill otherwise — so trusting the parsed values would mark
/// exactly the malformed skills as disclosable and drop their instructions. This costs one small file read
/// per skill when an agent context is built.
/// </para>
/// <para>
/// Field validity is delegated to <see cref="AgentSkillFrontmatter.ValidateName"/> and
/// <see cref="AgentSkillFrontmatter.ValidateDescription"/> — the framework's own public validators, which
/// are the same ones its loader calls. That keeps the kebab-case rule, the length caps, and any future
/// change to them in one place instead of duplicated here.
/// </para>
/// <para>
/// Three loader behaviours still have to be mirrored, because they are <c>private const</c> or inline in
/// <c>AgentFileSkillsSource</c> as of <c>Microsoft.Agents.AI</c> <b>1.13.0</b> and cannot be read off the
/// SDK: the <c>SKILL.md</c> filename, the frontmatter block shape, and the requirement that the
/// frontmatter <c>name</c> ordinal-equals the containing directory's name. Plus
/// <see cref="FrameworkSearchDepth"/>, the directory-search depth — the harness's own
/// <c>SkillMetadataRegistry</c> searches one level deeper, so a skill it discovers can legitimately be
/// invisible to the framework. <b>Re-check these on every SDK bump.</b>
/// <c>AgentExecutionContextFactoryProgressiveDisclosureTests</c> exercises the real loader end to end and
/// is the guard that would catch a divergence.
/// </para>
/// </remarks>
public static partial class FrameworkSkillCoverage
{
    /// <summary>
    /// The directory-search depth <c>AgentFileSkillsSource</c> applies below each wired root
    /// (<c>MaxSkillDirectorySearchDepth</c> in the SDK). A directory deeper than this is never scanned.
    /// </summary>
    public const int FrameworkSearchDepth = 2;

    /// <summary>The skill manifest filename the framework loader requires, verbatim.</summary>
    private const string SkillFileName = "SKILL.md";

    /// <summary>
    /// Matches the leading <c>---</c>-delimited frontmatter block. Mirrors the loader's own pattern,
    /// including the optional byte-order mark, so a file it considers unparseable is unparseable here too.
    /// </summary>
    [GeneratedRegex(@"\A﻿?^---\s*$(.+?)^---\s*$",
        RegexOptions.Multiline | RegexOptions.Singleline, matchTimeoutMilliseconds: 5000)]
    private static partial Regex FrontmatterRegex();

    /// <summary>
    /// Matches one top-level <c>key: value</c> pair, quoted or bare. Mirrors the loader's pattern.
    /// </summary>
    [GeneratedRegex(@"^([\w-]+)\s*:\s*(?:[""'](.+?)[""']|(.+?))\s*$",
        RegexOptions.Multiline, matchTimeoutMilliseconds: 5000)]
    private static partial Regex KeyValueRegex();

    /// <summary>
    /// Returns the ids of the skills in <paramref name="skills"/> that the framework provider — wired
    /// over <paramref name="wiredPaths"/> — will load, and whose bodies may therefore be dropped from the
    /// static system prompt in favour of on-demand <c>load_skill</c> disclosure.
    /// </summary>
    /// <param name="skills">The skills composing this agent.</param>
    /// <param name="wiredPaths">
    /// The exact paths handed to <c>AgentSkillsProviderBuilder.UseFileSkill</c>. Passing anything other
    /// than that same list makes the result meaningless — the two must be derived from one resolution.
    /// </param>
    /// <returns>
    /// The covered skill ids, compared ordinally-ignoring-case to match skill-id handling elsewhere.
    /// Empty when no provider is wired (<paramref name="wiredPaths"/> empty), which correctly forces
    /// every body to stay in the prompt.
    /// </returns>
    public static IReadOnlySet<string> SelectDisclosable(
        IReadOnlyList<SkillDefinition> skills,
        IReadOnlyList<string> wiredPaths)
    {
        ArgumentNullException.ThrowIfNull(skills);
        ArgumentNullException.ThrowIfNull(wiredPaths);

        var disclosable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (wiredPaths.Count == 0)
            return disclosable;

        var normalizedRoots = wiredPaths.Select(PathScope.Normalize).ToList();

        foreach (var skill in skills)
        {
            if (!string.IsNullOrEmpty(skill.Id) && IsDisclosable(skill, normalizedRoots))
                disclosable.Add(skill.Id);
        }

        return disclosable;
    }

    /// <summary>
    /// Applies the loader's rules to a single skill. Kept separate from <see cref="SelectDisclosable"/>
    /// so each rule can be asserted in isolation.
    /// </summary>
    private static bool IsDisclosable(SkillDefinition skill, IReadOnlyList<string> normalizedRoots)
    {
        var directory = skill.BaseDirectory;
        if (string.IsNullOrEmpty(directory))
            return false;

        var manifestPath = Path.Combine(directory, SkillFileName);
        if (!File.Exists(manifestPath))
            return false;

        var normalizedDirectory = PathScope.Normalize(directory);
        if (!normalizedRoots.Any(root => IsWithinSearchDepth(normalizedDirectory, root)))
            return false;

        return DeclaresLoadableFrontmatter(manifestPath, Path.GetFileName(normalizedDirectory));
    }

    /// <summary>
    /// Returns <see langword="true"/> when the manifest at <paramref name="manifestPath"/> declares a
    /// <c>name</c> and <c>description</c> the framework will accept, and that name ordinal-equals
    /// <paramref name="expectedName"/> (the skill directory's own name). Any read or parse failure returns
    /// <see langword="false"/>, keeping the body in the prompt.
    /// </summary>
    private static bool DeclaresLoadableFrontmatter(string manifestPath, string expectedName)
    {
        string? name;
        string? description;

        try
        {
            var frontmatter = FrontmatterRegex().Match(File.ReadAllText(manifestPath));
            if (!frontmatter.Success)
                return false;

            var fields = ReadScalars(frontmatter.Groups[1].Value.Trim());
            fields.TryGetValue("name", out name);
            fields.TryGetValue("description", out description);
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or RegexMatchTimeoutException)
        {
            return false;
        }

        if (!AgentSkillFrontmatter.ValidateName(name, out _) ||
            !AgentSkillFrontmatter.ValidateDescription(description, out _))
        {
            return false;
        }

        // Ordinal, matching the loader. A case-only difference is a rejection there, so it must be one
        // here too — treating it as covered is exactly how a skill loses its instructions silently.
        return string.Equals(name, expectedName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads every top-level <c>key: value</c> scalar from a frontmatter block. A later declaration of the
    /// same key wins, matching the loader's last-assignment-wins loop.
    /// </summary>
    private static Dictionary<string, string> ReadScalars(string yaml)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in KeyValueRegex().Matches(yaml))
            fields[match.Groups[1].Value] = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;

        return fields;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="normalizedDirectory"/> is
    /// <paramref name="normalizedRoot"/> itself, or lies no more than
    /// <see cref="FrameworkSearchDepth"/> directory levels beneath it.
    /// </summary>
    private static bool IsWithinSearchDepth(string normalizedDirectory, string normalizedRoot)
    {
        if (!PathScope.IsSameOrUnderNormalized(normalizedDirectory, normalizedRoot))
            return false;

        // A path wired directly as a root is the skill directory itself — depth 0.
        if (normalizedDirectory.Length == normalizedRoot.Length)
            return true;

        var relative = normalizedDirectory[normalizedRoot.Length..];
        var depth = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries).Length;

        return depth <= FrameworkSearchDepth;
    }
}
