namespace Application.AI.Common.Services.Governance;

/// <summary>
/// The one "cut this text to a ceiling and say so with a marker" primitive every trust-boundary
/// truncation call site shares (#467/#470).
/// </summary>
/// <remarks>
/// <para>
/// Before this type existed, the same shape — cut at a length, append a marker, report whether
/// anything was cut — had been independently rewritten at least three times: <c>ReportedFailureText.Cap</c>
/// (a fixed 4096-char ceiling that appended its marker <em>after</em> cutting to the ceiling, so its real
/// worst-case output ran the marker's length past the nominal cap), <c>DirectToolInvoker.Response.cs</c>'s
/// <c>ScrubAndBound</c>/<c>FinalCut</c> (a caller-supplied ceiling that correctly reserves room for the
/// marker, but never checked for a split surrogate pair), and <see cref="OpenTelemetry.Processors.AgentFrameworkSpanProcessor"/>'s
/// inline truncation (same missing surrogate check, and a third, differently-spelled marker). Each
/// difference was accidental, not a deliberate design choice for that call site — three copies drifting
/// under maintenance, not three requirements.
/// </para>
/// <para>
/// <strong>Never splits a surrogate pair.</strong> A cut that would land inside one backs off by one
/// character instead, so the result is always a well-formed string. This is the one property worth
/// stating explicitly: two of the three prior implementations didn't have it.
/// </para>
/// </remarks>
public static class BoundedText
{
    /// <summary>
    /// Cuts <paramref name="text"/> to at most <paramref name="ceiling"/> characters (marker included),
    /// if it exceeds that length.
    /// </summary>
    /// <param name="text">The text to bound.</param>
    /// <param name="ceiling">The maximum total length of the result, inclusive of <paramref name="marker"/>.</param>
    /// <param name="marker">
    /// Appended when a cut occurs, so the cut is visible in the text itself. When <paramref name="ceiling"/>
    /// is not larger than the marker's own length, the marker is dropped rather than overshooting the
    /// ceiling to fit it — the ceiling is the promise a caller sizing a downstream field is relying on.
    /// </param>
    /// <param name="alwaysEmbedMarker">
    /// When <see langword="true"/>, embeds <paramref name="marker"/> even when <paramref name="text"/>
    /// is already within <paramref name="ceiling"/> on its own, cutting only as much of
    /// <paramref name="text"/> as is needed to make room for it (#521's droppedByPreCut-only case: a
    /// truncation happened upstream that this call alone would not otherwise see, so the normal
    /// "nothing to cut, nothing to say" default would silently drop the marker instead of the caller's
    /// own already-known truncation signal). The returned <c>Truncated</c> is <see langword="false"/> in
    /// that append-only sub-case, since <paramref name="text"/> itself was never cut — callers using
    /// this flag already carry their own truncation signal from elsewhere and are expected to OR it in,
    /// not read it off this return.
    /// </param>
    /// <returns>The (possibly cut) text, and whether <paramref name="text"/> itself was cut.</returns>
    public static (string Text, bool Truncated) Cap(
        string text, int ceiling, string marker, bool alwaysEmbedMarker = false)
    {
        if (!alwaysEmbedMarker && text.Length <= ceiling)
        {
            return (text, false);
        }

        if (alwaysEmbedMarker && text.Length + marker.Length <= ceiling)
        {
            return (text + marker, false);
        }

        var cutIndex = ceiling > marker.Length ? ceiling - marker.Length : ceiling;
        cutIndex = Math.Min(cutIndex, text.Length);
        if (cutIndex > 0 && char.IsHighSurrogate(text[cutIndex - 1]))
        {
            cutIndex--;
        }

        var result = ceiling > marker.Length
            ? string.Concat(text.AsSpan(0, cutIndex), marker)
            : text[..cutIndex];
        return (result, true);
    }
}
