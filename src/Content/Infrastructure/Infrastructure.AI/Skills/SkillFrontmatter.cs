using Domain.AI.Egress;
using Domain.AI.Skills;
using Domain.AI.Tools;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.AI.Skills;

/// <summary>
/// The YAML frontmatter of a SKILL.md, parsed once and read by name.
/// </summary>
/// <remarks>
/// <para>
/// Issue #258. This replaces roughly 440 lines of hand-rolled, indent-sensitive parsing that had
/// grown two near-identical block readers — one for <c>egress.allowlist</c>, one for <c>tools</c>.
/// Keeping them in step was the problem: a blank-line defect had to be fixed in both, and it was
/// only found in the second because a reviewer went looking. One of the two parses an outbound
/// network allowlist, where silently dropping an entry narrows a security control without saying so.
/// </para>
/// <para>
/// The comment that justified hand-rolling ("one nested block doesn't warrant a YAML dependency")
/// stopped being true once it was two blocks and 440 lines. YamlDotNet was already a pinned
/// dependency of this solution and already in use by the evaluation dataset loader, so this adds no
/// supply-chain surface. Every SKILL.md this repository ships or vendors — 22 of them, including
/// third-party ones from npm — parses cleanly under a strict YAML reader; that was measured before
/// the change, not assumed.
/// </para>
/// <para>
/// <strong>Unreadable frontmatter REFUSES the skill; it does not load a partial one.</strong> The
/// hand-rolled parser degraded per field, so a typo cost one field. A strict reader cannot do
/// that — one bad line fails the whole document — and degrading to an empty document would have
/// silently emptied <c>allowed-tools</c> and <c>egress</c> along with it. An empty allowlist is
/// not "no opinion": it means this skill contributes no tool ceiling, and a null egress manifest
/// means "inherit the global default". A typo would therefore have quietly widened the security
/// posture behind a warning nobody reads.
/// </para>
/// <para>
/// So <see cref="Load"/> throws instead, and both callers — <c>SkillMetadataRegistry</c> and
/// <c>NestedSkillScanner</c> — already catch per manifest and continue. The result is that one
/// unreadable skill is skipped and logged while every other skill still loads: loud, contained,
/// and never half-configured. The inputs this matters for are ordinary typos, not exotica: an
/// unquoted colon in a description, a tab used for indentation, or a duplicate key.
/// </para>
/// </remarks>
internal sealed class SkillFrontmatter
{
    private static readonly SkillFrontmatter EmptyDocument = new(null);

    private readonly YamlMappingNode? _root;

    private SkillFrontmatter(YamlMappingNode? root) => _root = root;

    /// <summary>
    /// Parses raw frontmatter text.
    /// </summary>
    /// <param name="frontmatter">The text between the opening and closing <c>---</c> markers.</param>
    /// <returns>
    /// The parsed document. An empty document when there is no frontmatter at all, or when the
    /// document's root is not a mapping — both mean "this manifest declares nothing", which is a
    /// legitimate state and not an error.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The frontmatter is present but is not valid YAML. Deliberately fatal for this manifest —
    /// see the remarks on <see cref="SkillFrontmatter"/> for why a partial load is the more
    /// dangerous outcome.
    /// </exception>
    internal static SkillFrontmatter Load(string? frontmatter)
    {
        if (string.IsNullOrWhiteSpace(frontmatter))
            return EmptyDocument;

        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(frontmatter));

            return stream.Documents.Count > 0 && stream.Documents[0].RootNode is YamlMappingNode root
                ? new SkillFrontmatter(root)
                : EmptyDocument;
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new InvalidOperationException(
                "SKILL.md frontmatter is not valid YAML. The skill is not loaded rather than " +
                "loaded with missing fields, because the fields that would go missing include " +
                "allowed-tools and egress. " + ex.Message,
                ex);
        }
    }

    /// <summary>
    /// A scalar value by key, or null when the key is absent, is not a scalar, or is empty.
    /// Empty and absent are deliberately the same answer: the callers all treat "" as "not set".
    /// </summary>
    internal string? String(string key)
    {
        var value = Node(key) is YamlScalarNode scalar ? scalar.Value : null;
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// A sequence of scalars by key, or an empty list when the key is absent or not a sequence.
    /// Accepts both inline (<c>[a, b]</c>) and block form — the representation model does not
    /// distinguish them, which is most of the point of this change.
    /// </summary>
    internal IList<string> StringList(string key) => Node(key) is YamlSequenceNode sequence
        ? [.. Scalars(sequence)]
        : [];

    /// <summary>
    /// A nested block of scalar key/value pairs (e.g. <c>metadata:</c>), or null when the key is
    /// absent or the block has no scalar entries.
    /// </summary>
    /// <remarks>
    /// Non-scalar children are skipped. This is a deliberate CHANGE: the hand-rolled parser
    /// flattened them, so <c>metadata: {author: a, nested: {key: b}}</c> used to yield
    /// <c>author=a, nested="", key=b</c> — the nested key promoted to the top level and its parent
    /// left as an empty string. Nothing depended on that, and a flattened key silently colliding
    /// with a real one is worse than an absent one.
    /// </remarks>
    internal Dictionary<string, string>? ScalarBlock(string key)
    {
        if (Node(key) is not YamlMappingNode block)
            return null;

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (childKey, childValue) in block.Children)
        {
            if (childKey is YamlScalarNode { Value: { Length: > 0 } name } &&
                childValue is YamlScalarNode scalar)
            {
                result[name] = scalar.Value ?? string.Empty;
            }
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// The per-skill outbound egress allowlist. Null when <c>egress</c> is absent — the skill then
    /// inherits the global default with no additions. An empty manifest when <c>egress</c> is
    /// present but declares no allowlist, which is a deliberate "add nothing".
    /// </summary>
    /// <remarks>
    /// Entries carrying none of the four recognised keys are dropped rather than emitted blank: an
    /// entry that matches nothing is noise in a security decision. SEMANTIC validation — wildcard
    /// placement, permitted schemes, valid ports — belongs to <c>EgressManifestValidator</c> in the
    /// Application layer, not here.
    /// </remarks>
    internal EgressManifest? Egress()
    {
        if (Node("egress") is not YamlMappingNode egress)
            return null;

        if (Child(egress, "allowlist") is not YamlSequenceNode allowlist)
            return new EgressManifest { Allowlist = [] };

        var entries = new List<EgressAllowlistEntry>();
        foreach (var item in allowlist.Children.OfType<YamlMappingNode>())
        {
            var entry = new EgressAllowlistEntry
            {
                Host = ChildScalar(item, "host"),
                HostPattern = ChildScalar(item, "hostPattern"),
                Schemes = Child(item, "schemes") is YamlSequenceNode s ? [.. Scalars(s)] : [],
                Ports = Child(item, "ports") is YamlSequenceNode p ? [.. Integers(p)] : []
            };

            if (entry.Host is not null || entry.HostPattern is not null ||
                entry.Schemes.Count > 0 || entry.Ports.Count > 0)
            {
                entries.Add(entry);
            }
        }

        return new EgressManifest { Allowlist = entries };
    }

    /// <summary>
    /// The structured <c>tools:</c> block. Null when the key is absent or yields no usable entry,
    /// which keeps <c>SkillDefinition.HasToolDeclarations</c> — and therefore
    /// <c>SkillDefinition.Mode</c> — reading "this skill declared nothing".
    /// </summary>
    /// <remarks>
    /// An entry with no <c>name</c> is dropped rather than emitted nameless: an empty name cannot
    /// resolve, and <c>ToolChainBuilder</c> would report it far from the manifest that caused it.
    /// A missing <c>optional</c> means REQUIRED, matching the domain default — the direction that
    /// fails loudly at resolution rather than silently dropping a tool the skill depends on.
    /// </remarks>
    internal IList<ToolDeclaration>? ToolDeclarations()
    {
        if (Node("tools") is not YamlSequenceNode tools)
            return null;

        var declarations = new List<ToolDeclaration>();
        foreach (var item in tools.Children.OfType<YamlMappingNode>())
        {
            var name = ChildScalar(item, "name");
            if (string.IsNullOrWhiteSpace(name))
                continue;

            declarations.Add(new ToolDeclaration
            {
                Name = name,
                Description = ChildScalar(item, "description") ?? string.Empty,
                WhenToUse = ChildScalar(item, "when-to-use") ?? string.Empty,
                WhenNotToUse = ChildScalar(item, "when-not-to-use") ?? string.Empty,
                Fallback = ChildScalar(item, "fallback"),
                Condition = ChildScalar(item, "condition"),
                Optional = Boolean(ChildScalar(item, "optional")),
                CallOncePerConversation = Boolean(ChildScalar(item, "call-once-per-conversation")),
                Operations = Child(item, "operations") is YamlSequenceNode ops ? [.. Scalars(ops)] : []
            });
        }

        return declarations.Count > 0 ? declarations : null;
    }

    private YamlNode? Node(string key) => _root is null ? null : Child(_root, key);

    /// <remarks>
    /// Key lookup is case-insensitive because the hand-rolled parser's was, and shipped manifests
    /// are not consistent about it.
    /// </remarks>
    private static YamlNode? Child(YamlMappingNode node, string key)
    {
        foreach (var (childKey, childValue) in node.Children)
        {
            if (childKey is YamlScalarNode scalar &&
                string.Equals(scalar.Value, key, StringComparison.OrdinalIgnoreCase))
            {
                return childValue;
            }
        }

        return null;
    }

    private static string? ChildScalar(YamlMappingNode node, string key)
        => Child(node, key) is YamlScalarNode { Value: { Length: > 0 } value } ? value : null;

    private static IEnumerable<string> Scalars(YamlSequenceNode sequence)
        => sequence.Children
            .OfType<YamlScalarNode>()
            .Select(n => n.Value)
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!);

    private static IEnumerable<int> Integers(YamlSequenceNode sequence)
        => sequence.Children
            .OfType<YamlScalarNode>()
            .Select(n => int.TryParse(
                n.Value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : (int?)null)
            .Where(v => v.HasValue)
            .Select(v => v!.Value);

    /// <summary>
    /// Reads a YAML boolean, accepting <c>true</c> and <c>yes</c>. Anything else — including a typo
    /// — is false, which for <c>optional</c> means REQUIRED: a loud resolution failure rather than
    /// a tool quietly dropped from the chain.
    /// </summary>
    private static bool Boolean(string? value)
        => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
}
