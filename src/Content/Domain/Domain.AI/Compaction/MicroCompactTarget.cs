namespace Domain.AI.Compaction;

/// <summary>
/// Identifies tool result types eligible for lightweight micro-compaction.
/// These results can be replaced with compact summaries without an LLM call.
/// </summary>
public enum MicroCompactTarget
{
    /// <summary>File read operations — replaceable with "[file previously read: {path}]".</summary>
    FileRead,

    /// <summary>Shell/bash output — replaceable with truncated preview.</summary>
    ShellOutput,

    /// <summary>Grep/search results — replaceable with match count summary.</summary>
    GrepResult,

    /// <summary>Glob/file listing results — replaceable with file count summary.</summary>
    GlobResult,

    /// <summary>Web fetch results — replaceable with URL + truncated preview.</summary>
    WebFetch,

    /// <summary>Any tool result exceeding the size threshold.</summary>
    LargeToolResult
}
