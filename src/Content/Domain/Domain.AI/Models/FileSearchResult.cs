namespace Domain.AI.Models;

/// <summary>
/// Represents a search result from a file content search operation,
/// including the matched file path, context snippet, and line number.
/// </summary>
public record FileSearchResult
{
    /// <summary>Gets the file path that matched the search.</summary>
    public required string FilePath { get; init; }

    /// <summary>Gets a snippet of the matching content for context.</summary>
    public required string Snippet { get; init; }

    /// <summary>Gets the line number where the match was found, if available.</summary>
    public int? LineNumber { get; init; }
}
