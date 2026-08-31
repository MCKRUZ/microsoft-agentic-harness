namespace Domain.AI.Context;

/// <summary>
/// One bounded page read back from a result <c>IToolResultStore</c> spilled to disk, in character
/// offsets into the stored text (#563).
/// </summary>
/// <remarks>
/// Deliberately the only shape <c>IToolResultStore</c> exposes for reading a spilled result back —
/// there is no whole-file read. The stored copy is the tool's raw, untreated output and can be up
/// to <c>ToolResultStorageConfig.MaxSpillChars</c> characters; handing an unbounded string to a
/// caller would reproduce the exact problem this type exists to close, one layer later.
/// </remarks>
public sealed record ToolResultPage
{
    /// <summary>The page's text, starting at the offset it was requested with.</summary>
    public required string Text { get; init; }

    /// <summary>
    /// The offset to request next to continue reading, i.e. the offset this page ended at. Equal to
    /// <see cref="TotalChars"/> on the final page.
    /// </summary>
    public required int NextOffset { get; init; }

    /// <summary>The stored result's total length, so a caller can report progress.</summary>
    public required int TotalChars { get; init; }

    /// <summary>Whether calling again with <see cref="NextOffset"/> would return more text.</summary>
    public bool HasMore => NextOffset < TotalChars;

    /// <summary>
    /// Whether the caller reading this page must redact it before showing it to a model (security
    /// review finding on #563). The classification decision that gates a tool's own output
    /// (<c>ToolCallAdmission.RedactsOutput</c>) is resolved from <em>whichever tool is currently
    /// executing</em> — for a fetch, that is <c>tool_result_fetch</c> itself, not the tool whose
    /// output was originally spilled. <c>tool_result_fetch</c>'s own arguments resolve to no known
    /// asset, which a data-classification policy typically treats as "allow" by default — so without
    /// this flag, a result that was correctly redacted going TO the model on its first call would
    /// reach the model unredacted on a later <c>tool_result_fetch</c> call for the very same content.
    /// Set from the originating call's own redaction verdict at spill time and carried with the
    /// stored bytes, so the decision travels with the data instead of being re-derived from a caller
    /// identity that cannot know it.
    /// </summary>
    public bool RedactOnRetrieve { get; init; }
}
