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
}
