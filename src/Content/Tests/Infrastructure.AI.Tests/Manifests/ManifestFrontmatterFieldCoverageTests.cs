using System.Text.RegularExpressions;
using Application.Common.Helpers;
using FluentAssertions;
using Tests.Common;
using Xunit;

namespace Infrastructure.AI.Tests.Manifests;

/// <summary>
/// Asserts that every top-level frontmatter field shipped in a real <c>SKILL.md</c> or
/// <c>AGENT.md</c> manifest is actually read by <c>SkillMetadataParser</c> or
/// <c>AgentMetadataParser</c> — not silently discarded.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The defect this exists to catch (issue #355).</strong> Three agent-persona files were
/// named and foldered as skills, and every field above their <c>---</c> was deleted at load by a
/// hand-rolled loader that kept only the markdown body. A consumer reading a tool declaration with
/// a documented fallback reasonably concluded it was wired up; it was wired to nothing. The dead
/// files are gone (deleted, not patched — the real agents already live correctly at the repo
/// root), but nothing stopped a manifest from shipping an unread field again. This is the
/// repeatable check issue #355 asked for, in the shape of
/// <c>LearningsWriteGateCoverageTests</c>/<c>SecurityControlHasACallerTests</c>: a source scan,
/// because a behavioural test can only cover a field someone thought to write a test for, and the
/// failure this guards against is precisely a field nobody thought about.
/// </para>
/// <para>
/// <strong>The "known-read" keys are derived from the parser source, not hand-typed.</strong> A
/// static allow-list a human copies once has no tether back to the parser — if a future edit stops
/// reading a field, the list still says it's read, and this test would keep passing while silently
/// reproducing the exact defect it exists to catch, just for a field dropped by omission instead of
/// a rename. <see cref="SkillReadKeys"/>/<see cref="AgentReadKeys"/> instead regex-scan the actual
/// parser source files at test-run time, the same way <c>LearningsWriteGateCoverageTests</c> scans
/// for the literal call shape that proves a handler consults its gate, rather than maintaining a
/// list of "handlers already known to be gated".
/// </para>
/// <para>
/// <strong>Scan scope.</strong> Every <c>SKILL.md</c> under <c>skills/</c> and
/// <c>plugins/*/skills/*</c>, and every <c>AGENT.md</c> under <c>agents/</c> — the exact
/// directories <c>SkillMetadataRegistry</c>/<c>NestedSkillScanner</c> and
/// <c>AgentMetadataRegistry</c> walk at runtime, so this test sees what production actually loads.
/// The vendored <c>.claude/skills/gitnexus/**</c> files are Claude-Code-consumed, not
/// harness-consumed, and are outside all three scanned roots by construction.
/// </para>
/// <para>
/// <strong>If this fails,</strong> the answer is the same as issue #355's own acceptance
/// criterion: either wire the field into the parser, delete it from the manifest, or — only with a
/// stated reason — add it to <see cref="Exempt"/>. Silence is not a fourth option.
/// </para>
/// </remarks>
public sealed class ManifestFrontmatterFieldCoverageTests
{
    /// <summary>
    /// SKILL.md keys shipped today with no parser yet. Each entry needs the reason an unread field
    /// is correct as shipped, not silently deleted or scope-crept into a new feature by this test.
    /// </summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.OrdinalIgnoreCase)
    {
        ["denied-tools"] = "real declared capability (5 plugin skills), no parser yet — tracked, not a #355 fix",
        ["sandbox-required"] = "real declared capability (5 plugin skills), no parser yet — tracked, not a #355 fix",
    };

    private static readonly Regex TopLevelKey = new(@"^(?<key>[A-Za-z][\w-]*)\s*:", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Matches a call like <c>frontmatter.String("category")</c> or <c>.ScalarBlock("metadata")</c>.</summary>
    private static readonly Regex FrontmatterKeyCall = new(
        @"\.(?:String|StringList|ScalarBlock)\(""(?<key>[\w-]+)""\)", RegexOptions.Compiled);

    /// <summary>
    /// Matches <c>SkillFrontmatter</c>'s internal top-level lookup, <c>Node("tools")</c> /
    /// <c>Node("egress")</c> — the two keys read via a dedicated method rather than a generic
    /// <c>.String</c>/<c>.StringList</c> call, so <see cref="FrontmatterKeyCall"/> alone would miss
    /// them. Anchored to exactly one argument so it cannot also match the two-argument
    /// <c>Child(node, "name")</c>/<c>ChildScalar(node, "name")</c> helpers, which read keys nested
    /// *inside* a <c>tools[]</c>/<c>egress.allowlist[]</c> entry rather than top-level manifest keys.
    /// </summary>
    private static readonly Regex NodeKeyCall = new(@"\bNode\(""(?<key>[\w-]+)""\)", RegexOptions.Compiled);

    /// <summary>Matches <c>ParseString(yaml, "category")</c> / <c>ParseList(yaml, "tags")</c>.</summary>
    private static readonly Regex AgentParseKeyCall = new(
        @"\bParse(?:String|List)\(\w+,\s*""(?<key>[\w-]+)""\)", RegexOptions.Compiled);

    private static readonly HashSet<string> SkillReadKeys = DeriveSkillReadKeys();
    private static readonly HashSet<string> AgentReadKeys = DeriveAgentReadKeys();

    [Fact]
    public void EverySkillManifestField_IsReadOrExempt()
    {
        var offenders = FindOffenders(SkillFiles(), key => SkillReadKeys.Contains(key) || Exempt.ContainsKey(key));

        offenders.Should().BeEmpty(
            "a SKILL.md field with no parser is a declaration nobody reads — either wire it into "
            + "SkillMetadataParser, delete it from the manifest, or add a reasoned entry to Exempt. "
            + "Found: " + string.Join(", ", offenders));
    }

    [Fact]
    public void EveryAgentManifestField_IsRead()
    {
        var offenders = FindOffenders(AgentFiles(), AgentReadKeys.Contains);

        offenders.Should().BeEmpty(
            "an AGENT.md field with no parser is a declaration nobody reads — wire it into "
            + "AgentMetadataParser or delete it from the manifest. Found: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheScanWouldActuallyCatchAnUnreadField()
    {
        // Mutation control: an empty offender list above is only evidence if key extraction and
        // the allow-list check both do their job on the shape a real manifest is written in.
        const string manifest = """
            ---
            name: "example"
            totally_unread_field: "x"
            tools:
              - name: "inner"
                optional: true
            ---
            body
            """;

        var keys = TopLevelKeysInContent(manifest).ToArray();

        keys.Should().Contain("name", "a known-read key must be extracted");
        keys.Should().Contain("totally_unread_field", "the scan must extract keys it doesn't recognise, not just ones on the allow-list");
        keys.Should().NotContain("optional", "a nested key under tools[] is not a top-level field and must not be extracted as one");

        SkillReadKeys.Contains("totally_unread_field").Should().BeFalse("control: the made-up key must not already be on the allow-list");
        Exempt.ContainsKey("totally_unread_field").Should().BeFalse("control: the made-up key must not already be exempted");
    }

    [Fact]
    public void TheDerivedReadKeySets_ActuallyReflectTheParserSource()
    {
        // Mutation control for the derivation itself, not just the manifest-side extraction above.
        // "category" comes from a plain .String("category") call; "tools" and "egress" come from
        // SkillFrontmatter's internal Node("...") lookup, which a scan of SkillMetadataParser.cs
        // alone would miss — proving both regexes fired, not just the simpler one.
        SkillReadKeys.Should().Contain("category", "a plain .String(\"...\") call must be picked up");
        SkillReadKeys.Should().Contain("tools", "the Node(\"tools\") lookup inside SkillFrontmatter.cs must be picked up");
        SkillReadKeys.Should().Contain("egress", "the Node(\"egress\") lookup inside SkillFrontmatter.cs must be picked up");
        SkillReadKeys.Should().NotContain("optional", "a nested per-tool key read via Child(...)/ChildScalar(...) must not be mistaken for a top-level frontmatter key");
        SkillReadKeys.Should().NotContain(
            "denied-tools", "control: if this ever starts passing, SkillMetadataParser gained a real reader and the Exempt entry above is stale");

        AgentReadKeys.Should().Contain("skill", "the singular ParseString(yaml, \"skill\") fallback must be picked up alongside the plural \"skills\"");
    }

    private static IEnumerable<string> SkillFiles() =>
        Directory.EnumerateFiles(RepoRoot.Combine("skills"), "SKILL.md", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(RepoRoot.Combine("plugins"), "SKILL.md", SearchOption.AllDirectories));

    private static IEnumerable<string> AgentFiles() =>
        Directory.EnumerateFiles(RepoRoot.Combine("agents"), "AGENT.md", SearchOption.AllDirectories);

    /// <summary>
    /// Scans every file for its top-level frontmatter keys and returns the ones <paramref
    /// name="isAccountedFor"/> rejects, labelled by the containing manifest's folder name.
    /// </summary>
    private static List<string> FindOffenders(IEnumerable<string> files, Func<string, bool> isAccountedFor)
    {
        var fileList = files.ToArray();
        fileList.Should().NotBeEmpty("the scan must actually find the repo's manifest files");

        var offenders = new List<string>();

        foreach (var file in fileList)
        {
            foreach (var key in TopLevelKeysInFile(file))
            {
                if (!isAccountedFor(key))
                    offenders.Add($"{Path.GetFileName(Path.GetDirectoryName(file))}: '{key}'");
            }
        }

        return offenders;
    }

    private static IEnumerable<string> TopLevelKeysInFile(string filePath) =>
        TopLevelKeysInContent(File.ReadAllText(filePath));

    private static IEnumerable<string> TopLevelKeysInContent(string raw)
    {
        var (yaml, _) = YamlFrontmatterHelper.ExtractFrontmatter(raw);

        return TopLevelKey.Matches(yaml).Select(m => m.Groups["key"].Value).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> DeriveSkillReadKeys()
    {
        var parserSource = File.ReadAllText(RepoRoot.Combine(
            "src", "Content", "Infrastructure", "Infrastructure.AI", "Skills", "SkillMetadataParser.cs"));
        var frontmatterSource = File.ReadAllText(RepoRoot.Combine(
            "src", "Content", "Infrastructure", "Infrastructure.AI", "Skills", "SkillFrontmatter.cs"));

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in FrontmatterKeyCall.Matches(parserSource))
            keys.Add(match.Groups["key"].Value);
        foreach (Match match in NodeKeyCall.Matches(frontmatterSource))
            keys.Add(match.Groups["key"].Value);

        return keys;
    }

    private static HashSet<string> DeriveAgentReadKeys()
    {
        var parserSource = File.ReadAllText(RepoRoot.Combine(
            "src", "Content", "Infrastructure", "Infrastructure.AI", "Agents", "AgentMetadataParser.cs"));

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in AgentParseKeyCall.Matches(parserSource))
            keys.Add(match.Groups["key"].Value);

        return keys;
    }
}
