using Domain.Common.Helpers;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Helpers;

/// <summary>
/// Tests for <see cref="SecureInputValidatorHelper"/> covering all validation methods,
/// injection detection, path traversal, sanitization, and edge cases.
/// </summary>
public class SecureInputValidatorHelperLogicTests
{
    // ── ValidateInput ──

    [Fact]
    public void ValidateInput_NullInput_ReturnsFalse()
    {
        SecureInputValidatorHelper.ValidateInput(null!).Should().BeFalse();
    }

    [Fact]
    public void ValidateInput_EmptyInput_ReturnsFalse()
    {
        SecureInputValidatorHelper.ValidateInput("").Should().BeFalse();
    }

    [Fact]
    public void ValidateInput_WhitespaceInput_ReturnsFalse()
    {
        SecureInputValidatorHelper.ValidateInput("   ").Should().BeFalse();
    }

    [Fact]
    public void ValidateInput_ExceedsMaxLength_ReturnsFalse()
    {
        var longInput = new string('a', 1025);
        SecureInputValidatorHelper.ValidateInput(longInput).Should().BeFalse();
    }

    [Fact]
    public void ValidateInput_WithinCustomMaxLength_ReturnsTrue()
    {
        SecureInputValidatorHelper.ValidateInput("hello", maxLength: 10).Should().BeTrue();
    }

    [Fact]
    public void ValidateInput_ExceedsCustomMaxLength_ReturnsFalse()
    {
        SecureInputValidatorHelper.ValidateInput("hello world", maxLength: 5).Should().BeFalse();
    }

    [Theory]
    [InlineData("hello; rm -rf")]
    [InlineData("test|cat /etc/passwd")]
    [InlineData("exec `whoami`")]
    [InlineData("val$PATH")]
    [InlineData("redirect>output")]
    [InlineData("input<file")]
    public void ValidateInput_ShellMetaChars_ReturnsFalse(string input)
    {
        SecureInputValidatorHelper.ValidateInput(input).Should().BeFalse();
    }

    [Theory]
    [InlineData("cmd1&&cmd2")]
    [InlineData("cmd1||cmd2")]
    [InlineData("$(command)")]
    [InlineData("$()")]
    [InlineData("append>>file")]
    [InlineData("heredoc<<EOF")]
    public void ValidateInput_ShellInjectionPatterns_ReturnsFalse(string input)
    {
        SecureInputValidatorHelper.ValidateInput(input).Should().BeFalse();
    }

    [Fact]
    public void ValidateInput_SafeString_ReturnsTrue()
    {
        SecureInputValidatorHelper.ValidateInput("Hello World 123").Should().BeTrue();
    }

    [Fact]
    public void ValidateInput_SafeStringWithSpecialChars_ReturnsTrue()
    {
        SecureInputValidatorHelper.ValidateInput("test-name_v1.0:label").Should().BeTrue();
    }

    [Fact]
    public void ValidateInput_UnicodeChars_ReturnsFalse()
    {
        // Unicode outside allowed pattern
        SecureInputValidatorHelper.ValidateInput("\u00e9\u00e8\u00ea").Should().BeFalse();
    }

    // ── ValidateFilePath ──

    [Fact]
    public void ValidateFilePath_NullPath_ReturnsFalse()
    {
        SecureInputValidatorHelper.ValidateFilePath(null!).Should().BeFalse();
    }

    [Fact]
    public void ValidateFilePath_EmptyPath_ReturnsFalse()
    {
        SecureInputValidatorHelper.ValidateFilePath("").Should().BeFalse();
    }

    [Fact]
    public void ValidateFilePath_ExceedsMaxLength_ReturnsFalse()
    {
        var longPath = new string('a', 4097);
        SecureInputValidatorHelper.ValidateFilePath(longPath).Should().BeFalse();
    }

    [Fact]
    public void ValidateFilePath_NullByte_ReturnsFalse()
    {
        SecureInputValidatorHelper.ValidateFilePath("file\0name").Should().BeFalse();
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("..\\windows\\system32")]
    [InlineData("path/../../secret")]
    [InlineData("~/secret")]
    [InlineData("~\\secret")]
    [InlineData("path/%2e%2e/secret")]
    [InlineData("path/%2f/secret")]
    [InlineData("path/%5c/secret")]
    public void ValidateFilePath_PathTraversal_ReturnsFalse(string path)
    {
        SecureInputValidatorHelper.ValidateFilePath(path).Should().BeFalse();
    }

    [Fact]
    public void ValidateFilePath_ShellInjectionInPath_ReturnsFalse()
    {
        SecureInputValidatorHelper.ValidateFilePath("path;rm -rf /").Should().BeFalse();
    }

    [Fact]
    public void ValidateFilePath_ValidPath_ReturnsTrue()
    {
        SecureInputValidatorHelper.ValidateFilePath("src/Content/Tests/file.cs").Should().BeTrue();
    }

    // ── ValidateIdentifier ──

    [Fact]
    public void ValidateIdentifier_NullIdentifier_ReturnsFalse()
    {
        SecureInputValidatorHelper.ValidateIdentifier(null!).Should().BeFalse();
    }

    [Fact]
    public void ValidateIdentifier_EmptyIdentifier_ReturnsFalse()
    {
        SecureInputValidatorHelper.ValidateIdentifier("").Should().BeFalse();
    }

    [Fact]
    public void ValidateIdentifier_ExceedsDefaultMaxLength_ReturnsFalse()
    {
        var longId = new string('a', 129);
        SecureInputValidatorHelper.ValidateIdentifier(longId).Should().BeFalse();
    }

    [Fact]
    public void ValidateIdentifier_ExceedsCustomMaxLength_ReturnsFalse()
    {
        SecureInputValidatorHelper.ValidateIdentifier("abcdef", maxLength: 3).Should().BeFalse();
    }

    [Fact]
    public void ValidateIdentifier_ValidIdentifier_ReturnsTrue()
    {
        SecureInputValidatorHelper.ValidateIdentifier("my-tool_v1.0:latest").Should().BeTrue();
    }

    [Fact]
    public void ValidateIdentifier_WithSpaces_ReturnsFalse()
    {
        SecureInputValidatorHelper.ValidateIdentifier("my tool").Should().BeFalse();
    }

    [Fact]
    public void ValidateIdentifier_WithSpecialChars_ReturnsFalse()
    {
        SecureInputValidatorHelper.ValidateIdentifier("tool;inject").Should().BeFalse();
    }

    // ── Sanitize ──

    [Fact]
    public void Sanitize_NullInput_ReturnsEmpty()
    {
        SecureInputValidatorHelper.Sanitize(null!).Should().BeEmpty();
    }

    [Fact]
    public void Sanitize_EmptyInput_ReturnsEmpty()
    {
        SecureInputValidatorHelper.Sanitize("").Should().BeEmpty();
    }

    [Fact]
    public void Sanitize_SafeInput_ReturnsSameInput()
    {
        SecureInputValidatorHelper.Sanitize("hello world").Should().Be("hello world");
    }

    [Fact]
    public void Sanitize_RemovesShellMetaChars()
    {
        var result = SecureInputValidatorHelper.Sanitize("hello;world|test`cmd$var>out<in");

        result.Should().Be("helloworldtestcmdvaroutin");
    }

    [Fact]
    public void Sanitize_RemovesControlChars()
    {
        var result = SecureInputValidatorHelper.Sanitize("hello\x01\x02world");

        result.Should().Be("helloworld");
    }

    [Fact]
    public void Sanitize_PreservesWhitespace()
    {
        var result = SecureInputValidatorHelper.Sanitize("hello\tworld\nnext\rline");

        result.Should().Be("hello\tworld\nnext\rline");
    }

    [Fact]
    public void Sanitize_TruncatesToMaxLength()
    {
        var result = SecureInputValidatorHelper.Sanitize("abcdefghij", maxLength: 5);

        result.Should().Be("abcde");
    }

    [Fact]
    public void Sanitize_LargeInput_UsesHeapBuffer()
    {
        // Over 256 chars triggers heap allocation instead of stackalloc
        var input = new string('a', 300);
        var result = SecureInputValidatorHelper.Sanitize(input);

        result.Should().HaveLength(300);
    }
}
