using Domain.Common.Helpers;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Helpers;

/// <summary>
/// Tests for <see cref="IdentifierSanitizer"/> — the shared character-allowlist primitive
/// extracted from <c>BundleOwnedMcpToolNaming</c> and <c>ToolCallTranscriptExtractor</c>, which had
/// independently implemented the identical scan-and-replace logic.
/// </summary>
public sealed class IdentifierSanitizerTests
{
    [Fact]
    public void Sanitize_AlreadyCleanValue_ReturnsTheSameInstanceUnallocated()
    {
        var raw = "toolu_01A2b3C4-d5";

        var result = IdentifierSanitizer.Sanitize(raw);

        // ReferenceEquals, not just value equality: proves the fast path genuinely skips allocating a
        // replacement string for the common case rather than building an equal-but-distinct one.
        ReferenceEquals(result, raw).Should().BeTrue();
    }

    [Theory]
    [InlineData("get user", "get_user")]
    [InlineData("get.user", "get_user")]
    [InlineData("call#1", "call_1")]
    [InlineData("call$1", "call_1")]
    [InlineData("a:b", "a_b")]
    public void Sanitize_DisallowedCharacters_ReplacedWithUnderscore(string raw, string expected)
    {
        IdentifierSanitizer.Sanitize(raw).Should().Be(expected);
    }

    [Fact]
    public void Sanitize_EmptyString_ReturnsEmptyString()
    {
        IdentifierSanitizer.Sanitize(string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void Sanitize_OnlyDisallowedCharacters_AllReplaced()
    {
        IdentifierSanitizer.Sanitize("!@#$%").Should().Be("_____");
    }

    [Fact]
    public void Sanitize_PreservesLetterCaseAndDigitsAndAllowedPunctuation()
    {
        IdentifierSanitizer.Sanitize("AbC_123-xyZ").Should().Be("AbC_123-xyZ");
    }
}
