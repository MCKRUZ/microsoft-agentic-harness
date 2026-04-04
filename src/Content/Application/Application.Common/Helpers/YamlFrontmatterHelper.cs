namespace Application.Common.Helpers;

/// <summary>
/// Extracts YAML frontmatter from markdown content. SKILL.md and AGENT.md files
/// use YAML frontmatter delimited by <c>---</c> to declare metadata (name, description,
/// allowed-tools, effort, etc.) above the markdown body.
/// </summary>
/// <remarks>
/// <para>
/// This helper performs pure string splitting — no YAML deserialization.
/// YAML parsing (converting the extracted frontmatter string into typed objects)
/// is the responsibility of service implementations in the Infrastructure layer.
/// </para>
/// <para>
/// Expected format:
/// <code>
/// ---
/// name: my-skill
/// description: Does something useful
/// ---
///
/// # Skill Body
/// Markdown content here...
/// </code>
/// </para>
/// </remarks>
public static class YamlFrontmatterHelper
{
    private const string Delimiter = "---";

    /// <summary>
    /// Checks whether the markdown content contains YAML frontmatter.
    /// </summary>
    /// <param name="markdown">The markdown content to check.</param>
    /// <returns><c>true</c> if the content starts with <c>---</c> and contains a closing <c>---</c>.</returns>
    public static bool HasFrontmatter(string? markdown) =>
        TryLocateDelimiters(markdown, out _, out _);

    /// <summary>
    /// Extracts the YAML frontmatter and markdown body from the content.
    /// </summary>
    /// <param name="markdown">The markdown content with frontmatter.</param>
    /// <returns>
    /// A tuple of (<c>yaml</c>, <c>body</c>) where <c>yaml</c> is the frontmatter
    /// content between the <c>---</c> delimiters (without the delimiters), and
    /// <c>body</c> is the remaining markdown content.
    /// Returns (<c>empty</c>, <c>original</c>) if no frontmatter is found.
    /// </returns>
    /// <example>
    /// <code>
    /// var content = """
    ///     ---
    ///     name: code-review
    ///     effort: medium
    ///     ---
    ///
    ///     # Code Review Skill
    ///     Reviews code for quality.
    ///     """;
    ///
    /// var (yaml, body) = YamlFrontmatterHelper.ExtractFrontmatter(content);
    /// // yaml: "name: code-review\neffort: medium"
    /// // body: "\n# Code Review Skill\nReviews code for quality."
    /// </code>
    /// </example>
    public static (string Yaml, string Body) ExtractFrontmatter(string? markdown)
    {
        if (!TryLocateDelimiters(markdown, out var afterOpening, out var closingIndex))
            return (string.Empty, markdown ?? string.Empty);

        var trimmed = markdown!.TrimStart();
        var yaml = trimmed[afterOpening..closingIndex].Trim();
        var bodyStart = closingIndex + Delimiter.Length;
        var body = bodyStart < trimmed.Length ? trimmed[bodyStart..] : string.Empty;

        return (yaml, body);
    }

    private static bool TryLocateDelimiters(string? markdown, out int afterOpening, out int closingIndex)
    {
        afterOpening = 0;
        closingIndex = 0;

        if (string.IsNullOrWhiteSpace(markdown))
            return false;

        var trimmed = markdown.TrimStart();
        if (!trimmed.StartsWith(Delimiter))
            return false;

        afterOpening = trimmed.IndexOf('\n', trimmed.IndexOf(Delimiter, StringComparison.Ordinal)) + 1;
        if (afterOpening <= 0)
            return false;

        closingIndex = trimmed.IndexOf(Delimiter, afterOpening, StringComparison.Ordinal);
        return closingIndex >= 0;
    }
}
