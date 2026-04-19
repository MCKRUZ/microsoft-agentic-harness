using Domain.Common.Extensions;
using Domain.Common.Helpers;
using FluentAssertions;
using System.ComponentModel;
using Xunit;

namespace Domain.Common.Tests.Integration;

/// <summary>
/// Integration tests for Domain.Common helpers: <see cref="SecureInputValidatorHelper"/>,
/// <see cref="JsonAlphabetizerHelper"/>, <see cref="StringExtensions"/>,
/// and <see cref="EnumExtensions"/>.
/// </summary>
public class HelperIntegrationTests
{
    // ── SecureInputValidatorHelper ──

    [Theory]
    [InlineData("hello world", true)]
    [InlineData("agent-name_v2.0", true)]
    [InlineData("path/to/file.txt", true)]
    [InlineData("key=value", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ValidateInput_GeneralInputs_ReturnsExpected(string? input, bool expected)
    {
        SecureInputValidatorHelper.ValidateInput(input!, 1024).Should().Be(expected);
    }

    [Theory]
    [InlineData("rm -rf /; echo pwned", false)]
    [InlineData("normal && malicious", false)]
    [InlineData("inject $(whoami)", false)]
    [InlineData("pipe | command", false)]
    [InlineData("redirect > file", false)]
    [InlineData("backtick `cmd`", false)]
    public void ValidateInput_ShellInjection_Rejected(string input, bool expected)
    {
        SecureInputValidatorHelper.ValidateInput(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("C:\\Users\\test\\file.txt", true)]
    [InlineData("/var/log/app.log", true)]
    [InlineData("../../../etc/passwd", false)]
    [InlineData("path/../../secret", false)]
    [InlineData("~/sensitive", false)]
    [InlineData("path%2f..%2f..%2fsecret", false)]
    public void ValidateFilePath_PathTraversal_DetectedCorrectly(string path, bool expected)
    {
        SecureInputValidatorHelper.ValidateFilePath(path).Should().Be(expected);
    }

    [Fact]
    public void ValidateFilePath_NullByte_Rejected()
    {
        SecureInputValidatorHelper.ValidateFilePath("file\0.txt").Should().BeFalse();
    }

    [Fact]
    public void ValidateFilePath_TooLong_Rejected()
    {
        var longPath = new string('a', 4097);
        SecureInputValidatorHelper.ValidateFilePath(longPath).Should().BeFalse();
    }

    [Theory]
    [InlineData("my-tool", true)]
    [InlineData("agent_v2.1:main", true)]
    [InlineData("valid.identifier", true)]
    [InlineData("has spaces", false)]
    [InlineData("has;injection", false)]
    [InlineData("", false)]
    public void ValidateIdentifier_IdentifierPatterns_MatchCorrectly(string id, bool expected)
    {
        SecureInputValidatorHelper.ValidateIdentifier(id).Should().Be(expected);
    }

    [Fact]
    public void ValidateIdentifier_ExceedsMaxLength_Rejected()
    {
        var longId = new string('a', 129);
        SecureInputValidatorHelper.ValidateIdentifier(longId).Should().BeFalse();
    }

    [Fact]
    public void Sanitize_RemovesShellMetaChars()
    {
        var result = SecureInputValidatorHelper.Sanitize("hello; rm -rf /");

        result.Should().NotContain(";");
        result.Should().Contain("hello");
    }

    [Fact]
    public void Sanitize_RemovesControlCharacters()
    {
        var input = "normal\x01\x02text\x03";
        var result = SecureInputValidatorHelper.Sanitize(input);

        result.Should().Be("normaltext");
    }

    [Fact]
    public void Sanitize_PreservesWhitespace()
    {
        var result = SecureInputValidatorHelper.Sanitize("hello\tworld\nfoo");

        result.Should().Contain("\t");
        result.Should().Contain("\n");
    }

    [Fact]
    public void Sanitize_EmptyInput_ReturnsEmpty()
    {
        SecureInputValidatorHelper.Sanitize("").Should().BeEmpty();
        SecureInputValidatorHelper.Sanitize(null!).Should().BeEmpty();
    }

    [Fact]
    public void Sanitize_TruncatesToMaxLength()
    {
        var longInput = new string('a', 2000);
        var result = SecureInputValidatorHelper.Sanitize(longInput, 100);

        result.Length.Should().BeLessThanOrEqualTo(100);
    }

    // ── JsonAlphabetizerHelper ──

    [Fact]
    public void AlphabetizeProperties_SimpleObject_SortsKeys()
    {
        var json = """{"zebra": 1, "alpha": 2, "middle": 3}""";

        var result = JsonAlphabetizerHelper.AlphabetizeProperties(json);

        var keys = System.Text.Json.JsonDocument.Parse(result)
            .RootElement.EnumerateObject()
            .Select(p => p.Name)
            .ToList();

        keys.Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void AlphabetizeProperties_NestedObjects_SortsRecursively()
    {
        var json = """{"z": {"b": 1, "a": 2}, "a": {"d": 3, "c": 4}}""";

        var result = JsonAlphabetizerHelper.AlphabetizeProperties(json);
        var doc = System.Text.Json.JsonDocument.Parse(result);

        // Top-level keys should be sorted
        var topKeys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        topKeys.Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);

        // Nested keys should also be sorted
        var nestedKeys = doc.RootElement.GetProperty("a")
            .EnumerateObject().Select(p => p.Name).ToList();
        nestedKeys.Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void AlphabetizeProperties_ArrayElements_PreservesOrder()
    {
        var json = """{"items": [{"z": 1, "a": 2}, {"b": 3, "a": 4}]}""";

        var result = JsonAlphabetizerHelper.AlphabetizeProperties(json);
        var doc = System.Text.Json.JsonDocument.Parse(result);

        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
        items.Should().HaveCount(2);

        // Each object in the array should have sorted keys
        var firstKeys = items[0].EnumerateObject().Select(p => p.Name).ToList();
        firstKeys.Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void AlphabetizeProperties_NullOrWhitespace_Throws()
    {
        var act = () => JsonAlphabetizerHelper.AlphabetizeProperties(null!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AlphabetizeProperties_InvalidJson_ThrowsJsonException()
    {
        var act = () => JsonAlphabetizerHelper.AlphabetizeProperties("not json");

        act.Should().Throw<System.Text.Json.JsonException>();
    }

    // ── StringExtensions ──

    [Fact]
    public void Truncate_ShortString_ReturnsUnchanged()
    {
        "hello".Truncate(10).Should().Be("hello");
    }

    [Fact]
    public void Truncate_LongString_TruncatesWithEllipsis()
    {
        "hello world".Truncate(5).Should().Be("hello...");
    }

    [Fact]
    public void Truncate_ExactLength_ReturnsUnchanged()
    {
        "hello".Truncate(5).Should().Be("hello");
    }

    [Fact]
    public void Truncate_NullOrEmpty_ReturnsAsIs()
    {
        ((string)null!).Truncate(5).Should().BeNull();
        "".Truncate(5).Should().BeEmpty();
    }

    // ── EnumExtensions ──

    private enum TestEnum
    {
        [Description("First item description")]
        First,

        [Description("Second item description")]
        Second,

        NoDescription
    }

    [Fact]
    public void ToDescriptionString_WithAttribute_ReturnsDescription()
    {
        TestEnum.First.ToDescriptionString().Should().Be("First item description");
        TestEnum.Second.ToDescriptionString().Should().Be("Second item description");
    }

    [Fact]
    public void ToDescriptionString_WithoutAttribute_ReturnsEmpty()
    {
        TestEnum.NoDescription.ToDescriptionString().Should().BeEmpty();
    }

    [Fact]
    public void ToDescriptionString_CalledMultipleTimes_UsesCaching()
    {
        // Call multiple times to exercise the cache
        var first = TestEnum.First.ToDescriptionString();
        var second = TestEnum.First.ToDescriptionString();

        first.Should().Be(second);
        first.Should().Be("First item description");
    }
}
