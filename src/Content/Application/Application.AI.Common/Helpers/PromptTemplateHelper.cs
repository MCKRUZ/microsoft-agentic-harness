using System.Text.RegularExpressions;

namespace Application.AI.Common.Helpers;

/// <summary>
/// Lightweight Mustache-style template rendering for agent prompts.
/// Replaces <c>{{variable}}</c> placeholders with values from a dictionary.
/// </summary>
/// <remarks>
/// <para>
/// Designed for simple variable substitution in system prompts, skill instructions,
/// and tool descriptions. If you need conditionals, loops, or partials, upgrade to
/// a full template engine (Scriban, Handlebars) as an Infrastructure service.
/// </para>
/// <para>
/// Placeholder format: <c>{{name}}</c> — double curly braces, whitespace-trimmed.
/// Unresolved placeholders are left as-is (not removed) so they can be detected
/// via <see cref="HasUnresolvedPlaceholders"/>.
/// </para>
/// </remarks>
public static partial class PromptTemplateHelper
{
    /// <summary>
    /// Renders a template by replacing <c>{{key}}</c> placeholders with values
    /// from the provided dictionary. Keys are matched case-insensitively.
    /// Unresolved placeholders are left unchanged.
    /// </summary>
    /// <param name="template">The template string with <c>{{placeholders}}</c>.</param>
    /// <param name="variables">Key-value pairs for substitution.</param>
    /// <returns>The rendered string with resolved placeholders.</returns>
    /// <example>
    /// <code>
    /// var template = "You are {{agent_name}}. Available tools: {{tool_list}}.";
    /// var rendered = PromptTemplateHelper.Render(template, new Dictionary&lt;string, string&gt;
    /// {
    ///     ["agent_name"] = "planner",
    ///     ["tool_list"] = "file_system, web_fetch"
    /// });
    /// // "You are planner. Available tools: file_system, web_fetch."
    /// </code>
    /// </example>
    public static string Render(string template, IDictionary<string, string> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        if (string.IsNullOrEmpty(template) || variables.Count == 0)
            return template ?? string.Empty;

        // Ensure case-insensitive lookup regardless of caller's dictionary comparer
        var lookup = variables is Dictionary<string, string> dict
            && dict.Comparer == StringComparer.OrdinalIgnoreCase
                ? variables
                : new Dictionary<string, string>(variables, StringComparer.OrdinalIgnoreCase);

        return PlaceholderRegex().Replace(template, match =>
        {
            var key = match.Groups[1].Value.Trim();
            return lookup.TryGetValue(key, out var value)
                ? value
                : match.Value; // Leave unresolved placeholders as-is
        });
    }

    /// <summary>
    /// Extracts all placeholder names from a template.
    /// </summary>
    /// <param name="template">The template string to scan.</param>
    /// <returns>A list of unique placeholder names (without the <c>{{}}</c> delimiters).</returns>
    /// <example>
    /// <code>
    /// var placeholders = PromptTemplateHelper.ExtractPlaceholders(
    ///     "Hello {{name}}, your role is {{role}}.");
    /// // ["name", "role"]
    /// </code>
    /// </example>
    public static IReadOnlyList<string> ExtractPlaceholders(string? template)
    {
        if (string.IsNullOrEmpty(template))
            return [];

        return PlaceholderRegex()
            .Matches(template)
            .Select(m => m.Groups[1].Value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Checks whether a rendered template still contains unresolved <c>{{placeholders}}</c>.
    /// </summary>
    /// <param name="rendered">The rendered string to check.</param>
    /// <returns><c>true</c> if unresolved placeholders remain.</returns>
    public static bool HasUnresolvedPlaceholders(string? rendered) =>
        !string.IsNullOrEmpty(rendered) && PlaceholderRegex().IsMatch(rendered);

    [GeneratedRegex(@"\{\{\s*([\w.\-]+)\s*\}\}", RegexOptions.None)]
    private static partial Regex PlaceholderRegex();
}
