using System.Text.Json;
using System.Text.RegularExpressions;
using Domain.AI.Compression.Enums;

namespace Infrastructure.AI.Compression;

/// <summary>
/// Sniffs tool output content to infer <see cref="ToolOutputCategory"/>
/// when the tool does not declare one via <c>ITool.OutputCategory</c>.
/// Detection order: JSON → FileContent → Tabular → SearchResults → FreeText.
/// </summary>
public static partial class ContentTypeDetector
{
    [GeneratedRegex(@"^[\s]*[\w\./\\]+\.\w+:\d+:", RegexOptions.Multiline)]
    private static partial Regex FilePathLineNumberPattern();

    [GeneratedRegex(@"^.+\t.+\t.+$", RegexOptions.Multiline)]
    private static partial Regex TabDelimitedPattern();

    /// <summary>
    /// Detects the output category from content structure.
    /// Returns <see cref="ToolOutputCategory.FreeText"/> for null, empty, or unrecognized content.
    /// </summary>
    public static ToolOutputCategory Detect(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return ToolOutputCategory.FreeText;

        var trimmed = output.AsSpan().Trim();
        if (trimmed.Length == 0)
            return ToolOutputCategory.FreeText;

        if (IsJson(trimmed))
            return ToolOutputCategory.Json;

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        if (lines.Length >= 3 && FilePathLineNumberPattern().IsMatch(output))
            return ToolOutputCategory.FileContent;

        if (IsTabular(lines))
            return ToolOutputCategory.Tabular;

        if (IsRepeatedStructure(lines))
            return ToolOutputCategory.SearchResults;

        return ToolOutputCategory.FreeText;
    }

    private static bool IsJson(ReadOnlySpan<char> trimmed)
    {
        if ((trimmed[0] != '{' && trimmed[0] != '[') ||
            (trimmed[^1] != '}' && trimmed[^1] != ']'))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(trimmed.ToString());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsTabular(string[] lines)
    {
        if (lines.Length < 3)
            return false;

        var firstTabCount = lines[0].Count(c => c == '\t');
        if (firstTabCount < 2)
            return false;

        var matchingLines = lines.Count(l => l.Count(c => c == '\t') == firstTabCount);
        return matchingLines >= lines.Length * 0.7;
    }

    private static bool IsRepeatedStructure(string[] lines)
    {
        if (lines.Length < 10)
            return false;

        // Extract structural key from each line (leading alphabetic word, ignoring numbers)
        var keys = lines.Select(ExtractStructuralKey).ToArray();
        var dominantKey = keys
            .Where(k => k.Length >= 3)
            .GroupBy(k => k)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault()?.Key;

        if (dominantKey is null)
            return false;

        var matchCount = keys.Count(k => k.Equals(dominantKey, StringComparison.Ordinal));
        return matchCount >= lines.Length * 0.6;
    }

    private static string ExtractStructuralKey(string line)
    {
        // Extract the first run of non-digit, non-whitespace characters as the structural key.
        // "Result 1: ..." → "Result", "Error[42]:" → "Error", "  src/foo.cs:10:" → "src/foo.cs:"
        var span = line.AsSpan().TrimStart();
        var end = 0;
        while (end < span.Length && !char.IsWhiteSpace(span[end]) && !char.IsDigit(span[end]))
            end++;
        return end >= 3 ? span[..end].ToString() : string.Empty;
    }
}
