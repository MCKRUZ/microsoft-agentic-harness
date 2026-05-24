namespace Domain.AI.Compression.Enums;

/// <summary>
/// Classifies tool output content for compression strategy selection.
/// Tools declare this via <c>ITool.OutputCategory</c>; when not declared,
/// <c>ContentTypeDetector</c> infers it from content structure.
/// </summary>
public enum ToolOutputCategory
{
    /// <summary>Parseable JSON — API responses, structured data.</summary>
    Json = 0,

    /// <summary>Source code, configuration files, documents with line structure.</summary>
    FileContent = 1,

    /// <summary>Multiple results with repeated structure (search hits, log entries).</summary>
    SearchResults = 2,

    /// <summary>Tab/pipe-delimited rows with consistent column count.</summary>
    Tabular = 3,

    /// <summary>Unstructured prose, error output, logs without repeated structure.</summary>
    FreeText = 4
}
