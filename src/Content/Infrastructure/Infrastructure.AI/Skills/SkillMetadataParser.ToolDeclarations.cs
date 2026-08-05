using Domain.AI.Skills;
using Domain.AI.Tools;

namespace Infrastructure.AI.Skills;

/// <summary>
/// The <c>tools:</c> frontmatter block: the one harness field that is a list of maps rather than
/// a scalar or a flat list, split out from the main parser by responsibility.
/// </summary>
public sealed partial class SkillMetadataParser
{
    /// <summary>
    /// Parses the structured <c>tools:</c> block from YAML frontmatter into tool declarations.
    /// Returns null when the <c>tools</c> key is absent or yields no usable entry, which keeps
    /// <see cref="SkillDefinition.HasToolDeclarations"/> — and therefore
    /// <see cref="SkillDefinition.Mode"/> — reading "this skill declared nothing".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each list item is a map with keys <c>name</c>, <c>operations</c>, <c>optional</c>,
    /// <c>fallback</c>, <c>condition</c>, <c>description</c>, <c>when-to-use</c> and
    /// <c>when-not-to-use</c>. <c>operations</c> accepts both an inline array
    /// (<c>["read", "list"]</c>) and a block sequence, because the domain type's own
    /// documentation advertises the block form while every shipped manifest uses the inline one.
    /// </para>
    /// <para>
    /// Unknown <em>scalar</em> keys are ignored, so a manifest that adds one does not break an
    /// older parser. A nested map under an unknown key is NOT understood: its children are read
    /// as further keys of the same declaration, so a future field of that shape needs a case
    /// here rather than relying on the parser to skip it.
    /// </para>
    /// <para>
    /// An entry with no <c>name</c> is dropped rather than emitted nameless: an empty name
    /// cannot resolve, and <c>ToolChainBuilder</c> would report it far from the manifest that
    /// caused it. A missing <c>optional</c> means REQUIRED, matching the domain default — the
    /// direction that fails loudly at resolution rather than silently dropping a tool the skill
    /// depends on.
    /// </para>
    /// <para>
    /// Hand-rolled for the same reason as the egress block: the surrounding frontmatter parser is
    /// hand-rolled, and one nested block does not justify a YAML dependency.
    /// </para>
    /// </remarks>
    internal static IList<ToolDeclaration>? ParseToolDeclarations(string? frontmatter)
    {
        if (string.IsNullOrEmpty(frontmatter))
            return null;

        var lines = frontmatter.Split('\n');

        var toolsIdx = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
                continue;

            if (line.Trim().Equals("tools:", StringComparison.OrdinalIgnoreCase))
            {
                toolsIdx = i;
                break;
            }
        }

        if (toolsIdx < 0)
            return null;

        var declarations = ParseToolDeclarationItems(lines, toolsIdx);
        return declarations.Count > 0 ? declarations : null;
    }

    private static List<ToolDeclaration> ParseToolDeclarationItems(string[] lines, int toolsIdx)
    {
        var declarations = new List<ToolDeclaration>();

        // Establish the list-item indent from the first '- ' line under "tools:".
        var itemIndent = -1;
        for (var probe = toolsIdx + 1; probe < lines.Length; probe++)
        {
            var raw = lines[probe];
            if (IsBlank(raw))
                continue;

            var leading = CountLeadingSpaces(raw);
            if (leading == 0)
                return declarations; // out of the tools block entirely

            if (raw.TrimStart().StartsWith('-'))
            {
                itemIndent = leading;
                break;
            }

            return declarations; // indented but not a list item — malformed
        }

        if (itemIndent < 0)
            return declarations;

        var i = toolsIdx + 1;
        while (i < lines.Length)
        {
            var raw = lines[i];
            if (IsBlank(raw))
            {
                i++;
                continue;
            }

            if (CountLeadingSpaces(raw) < itemIndent)
                break;

            if (CountLeadingSpaces(raw) == itemIndent && raw.TrimStart().StartsWith('-'))
            {
                var (declaration, consumed) = ReadOneToolDeclaration(lines, i, itemIndent);
                if (declaration is not null)
                    declarations.Add(declaration);

                i += consumed;
            }
            else
            {
                // Unexpected line shape — skip defensively.
                i++;
            }
        }

        return declarations;
    }

    private static (ToolDeclaration? Declaration, int Consumed) ReadOneToolDeclaration(
        string[] lines,
        int startIdx,
        int itemIndent)
    {
        var declaration = new ToolDeclaration();

        // Set while `operations:` has been seen with no inline value, so the following '- item'
        // lines are read as its entries. Operations is the only key that can be a block sequence,
        // which is why one flag suffices.
        var operationsPending = false;

        var first = lines[startIdx].TrimStart();
        var firstAfterDash = first.Length > 1 ? first[1..].TrimStart() : string.Empty;
        if (!string.IsNullOrEmpty(firstAfterDash))
            operationsPending = ApplyDeclarationKvp(firstAfterDash, declaration);

        var i = startIdx + 1;
        while (i < lines.Length)
        {
            var raw = lines[i];
            if (IsBlank(raw))
            {
                i++;
                continue;
            }

            // Anything at or shallower than the item indent starts the next entry or ends the block.
            if (CountLeadingSpaces(raw) <= itemIndent)
                break;

            var trimmed = raw.TrimStart();

            if (operationsPending && trimmed.StartsWith('-'))
            {
                var item = trimmed[1..].Trim().Trim('"', '\'');
                if (!string.IsNullOrEmpty(item))
                    declaration.Operations.Add(item);

                i++;
                continue;
            }

            operationsPending = ApplyDeclarationKvp(trimmed, declaration);
            i++;
        }

        var consumed = i - startIdx;
        return string.IsNullOrWhiteSpace(declaration.Name)
            ? (null, consumed)
            : (declaration, consumed);
    }

    /// <summary>
    /// Applies one <c>key: value</c> line to <paramref name="declaration"/>. Returns true when the
    /// line opened <c>operations:</c> with no inline value, meaning the caller should read the
    /// following <c>- item</c> lines as its entries.
    /// </summary>
    private static bool ApplyDeclarationKvp(string trimmed, ToolDeclaration declaration)
    {
        var colon = trimmed.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
            return false;

        var key = trimmed[..colon].Trim();
        var value = trimmed[(colon + 1)..].Trim().Trim('"', '\'');

        if (key.Equals("name", StringComparison.OrdinalIgnoreCase))
            declaration.Name = value;
        else if (key.Equals("description", StringComparison.OrdinalIgnoreCase))
            declaration.Description = value;
        else if (key.Equals("fallback", StringComparison.OrdinalIgnoreCase))
            declaration.Fallback = string.IsNullOrEmpty(value) ? null : value;
        else if (key.Equals("condition", StringComparison.OrdinalIgnoreCase))
            declaration.Condition = string.IsNullOrEmpty(value) ? null : value;
        else if (key.Equals("optional", StringComparison.OrdinalIgnoreCase))
            declaration.Optional = ParseYamlBoolean(value);
        else if (key.Equals("when-to-use", StringComparison.OrdinalIgnoreCase))
            declaration.WhenToUse = value;
        else if (key.Equals("when-not-to-use", StringComparison.OrdinalIgnoreCase))
            declaration.WhenNotToUse = value;
        else if (key.Equals("operations", StringComparison.OrdinalIgnoreCase))
        {
            if (value.StartsWith('['))
                declaration.Operations = [.. ParseInlineStringArray(value)];
            else if (value.Length == 0)
                return true; // a block sequence follows
        }
        // Unknown scalar keys ignored — see the remarks on ParseToolDeclarations.

        return false;
    }

    /// <summary>
    /// Reads a YAML boolean, accepting <c>true</c> and <c>yes</c>. Anything else is false —
    /// for <c>optional</c> that means REQUIRED, so a typo surfaces as a loud resolution failure
    /// rather than a tool quietly dropped from the chain.
    /// </summary>
    private static bool ParseYamlBoolean(string value)
        => value.Equals("true", StringComparison.OrdinalIgnoreCase)
        || value.Equals("yes", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a line carries nothing an indent-sensitive parser should judge. Whitespace-only
    /// lines count as blank: a line of spaces has a leading-space count, and treating that as a
    /// real indent level ends the block early.
    /// </summary>
    private static bool IsBlank(string line) => string.IsNullOrWhiteSpace(line);
}
