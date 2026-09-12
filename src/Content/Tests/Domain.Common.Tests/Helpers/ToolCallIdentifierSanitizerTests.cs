using Domain.Common.Helpers;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Helpers;

/// <summary>
/// Tests for <see cref="ToolCallIdentifierSanitizer"/> — extracted from
/// <c>ToolCallTranscriptExtractor.SanitizeIdentifier</c> (#513) so the live AG-UI streaming path
/// (#556) can share the identical deterministic transform rather than re-deriving its own.
/// </summary>
public sealed class ToolCallIdentifierSanitizerTests
{
    [Fact]
    public void Sanitize_AlreadyCleanValue_ReturnsUnchangedFalseAndTheSameInstance()
    {
        var raw = "call_01A2b3C4-d5";

        var result = ToolCallIdentifierSanitizer.Sanitize(raw);

        result.Changed.Should().BeFalse();
        // Unwraps the SanitizedIdentifier's own string field for the reference check (#633) — boxing
        // the struct itself for ReferenceEquals would never match raw regardless of the underlying value.
        ReferenceEquals(result.Value.Value, raw).Should().BeTrue();
    }

    [Fact]
    public void Sanitize_DisallowedCharacters_ChangedTrueAndValueMatchesIdentifierShape()
    {
        var result = ToolCallIdentifierSanitizer.Sanitize("call#1;DROP TABLE conversations;--");

        result.Changed.Should().BeTrue();
        result.Value.ToString().Should().MatchRegex("^[A-Za-z0-9_-]+$");
    }

    [Fact]
    public void Sanitize_ValueOverMaxLength_TruncatesAndReportsChanged()
    {
        var raw = new string('a', ToolCallIdentifierSanitizer.MaxLength + 50);

        var result = ToolCallIdentifierSanitizer.Sanitize(raw);

        result.Changed.Should().BeTrue();
        result.Value.ToString().Length.Should().BeLessThanOrEqualTo(ToolCallIdentifierSanitizer.MaxLength);
    }

    [Fact]
    public void Sanitize_TwoDistinctRawValuesCollapsingToTheSameBase_ProduceDistinctSanitizedValues()
    {
        // The collision-guard hash suffix exists specifically so "call#1" and "call$1" -- which both
        // sanitize to "call_1" via the bare character-class scan -- stay distinguishable afterward.
        var first = ToolCallIdentifierSanitizer.Sanitize("call#1");
        var second = ToolCallIdentifierSanitizer.Sanitize("call$1");

        first.Value.Should().NotBe(second.Value);
    }

    [Fact]
    public void Sanitize_SameRawValueSanitizedTwice_ProducesTheIdenticalResult()
    {
        // #556's own correctness depends on this: the call-emit CallId and the result-emit CallId
        // for the same raw id must sanitize to the same value on both sides, or
        // ToolCallOrderingSink's plain string-equality correlation breaks.
        const string raw = "call#1;DROP TABLE conversations;--";

        var first = ToolCallIdentifierSanitizer.Sanitize(raw);
        var second = ToolCallIdentifierSanitizer.Sanitize(raw);

        first.Value.Should().Be(second.Value);
    }

    [Fact]
    public void Sanitize_EmptyString_ReturnsUnchanged()
    {
        var result = ToolCallIdentifierSanitizer.Sanitize(string.Empty);

        result.Changed.Should().BeFalse();
        result.Value.ToString().Should().BeEmpty();
    }

    [Fact]
    public void Sanitize_RawValueShapedLikeAnotherRawValuesRewrittenOutput_DoesNotCollideWithIt()
    {
        // #556 security review (both an original and a re-run security-reviewer agent independently
        // found this): the rewrite branch's own output (clean, short) is a fixed point of the
        // passthrough branch. Without a guard, sanitizing a raw value that already LOOKS like some
        // other raw value's rewritten output would pass through unchanged and collide with it --
        // reopening the exact "two distinct raw ids share one persisted/streamed CallId" hazard the
        // hash suffix exists to prevent, one level up. A raw CallId equal to the first call's
        // rewritten output is fully attacker-computable offline (SHA-256 is public).
        var first = ToolCallIdentifierSanitizer.Sanitize("call#1");
        first.Changed.Should().BeTrue("call#1 contains a disallowed character");

        var second = ToolCallIdentifierSanitizer.Sanitize(first.Value);

        second.Value.Should().NotBe(first.Value,
            "a distinct raw value shaped like the first one's rewritten output must not collide with it");
    }
}
