using Application.AI.Common.Services.Governance;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Services.Governance;

/// <summary>Tests for <see cref="BoundedText"/> (#467/#470).</summary>
public sealed class BoundedTextTests
{
    private const string Marker = "…[truncated]";

    [Fact]
    public void Cap_TextAtOrUnderCeiling_ReturnsUnchanged()
    {
        var (text, truncated) = BoundedText.Cap("hello", 10, Marker);

        text.Should().Be("hello");
        truncated.Should().BeFalse();
    }

    [Fact]
    public void Cap_TextOverCeiling_CutsAndAppendsMarker()
    {
        var input = new string('x', 100);
        var (text, truncated) = BoundedText.Cap(input, 20, Marker);

        truncated.Should().BeTrue();
        text.Length.Should().Be(20);
        text.Should().EndWith(Marker);
    }

    [Fact]
    public void Cap_CeilingNotLargerThanMarker_DropsMarkerRatherThanOvershoot()
    {
        var input = new string('x', 100);
        var (text, truncated) = BoundedText.Cap(input, Marker.Length, Marker);

        truncated.Should().BeTrue();
        text.Length.Should().Be(Marker.Length);
        text.Should().NotContain(Marker, "the ceiling is the promise; a marker that can't fit is dropped, not overshot");
    }

    /// <summary>
    /// #467: two of the three prior independent truncation implementations could split a UTF-16
    /// surrogate pair at the cut point, producing a malformed string. The shared primitive backs off
    /// by one character instead.
    /// </summary>
    [Fact]
    public void Cap_CutWouldSplitSurrogatePair_BacksOffByOneCharacter()
    {
        // U+1F600 (😀) is a surrogate pair in UTF-16. Position the ceiling so the naive cut point
        // (ceiling - marker.Length) lands exactly between the high and low surrogate.
        var emoji = "\U0001F600"; // 2 UTF-16 chars
        var input = new string('a', 10) + emoji + new string('b', 30);
        var ceiling = 10 + 1 + Marker.Length; // cut point would land inside the emoji's surrogate pair

        var (text, truncated) = BoundedText.Cap(input, ceiling, Marker);

        truncated.Should().BeTrue();
        // A well-formed string never ends with an unpaired high surrogate immediately before the
        // marker's first character.
        var markerStart = text.IndexOf(Marker, StringComparison.Ordinal);
        markerStart.Should().BeGreaterThan(0);
        char.IsHighSurrogate(text[markerStart - 1]).Should().BeFalse(
            "the cut must back off before a high surrogate rather than split the pair");
    }

    [Fact]
    public void Cap_EmptyText_ReturnsUnchanged()
    {
        var (text, truncated) = BoundedText.Cap(string.Empty, 10, Marker);

        text.Should().BeEmpty();
        truncated.Should().BeFalse();
    }
}
