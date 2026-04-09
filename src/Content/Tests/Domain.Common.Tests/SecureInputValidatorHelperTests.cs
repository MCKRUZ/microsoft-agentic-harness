using Domain.Common.Helpers;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests;

public class SecureInputValidatorHelperTests
{
    // --- ValidateInput ---

    [Theory]
    [InlineData("hello world")]
    [InlineData("some-identifier_v2")]
    [InlineData("path/to/file.txt")]
    public void ValidateInput_ValidInput_ReturnsTrue(string input)
    {
        SecureInputValidatorHelper.ValidateInput(input).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateInput_NullOrWhitespace_ReturnsFalse(string? input)
    {
        SecureInputValidatorHelper.ValidateInput(input!).Should().BeFalse();
    }

    [Fact]
    public void ValidateInput_ExceedsMaxLength_ReturnsFalse()
    {
        var longInput = new string('a', 1025);

        SecureInputValidatorHelper.ValidateInput(longInput).Should().BeFalse();
    }

    [Fact]
    public void ValidateInput_CustomMaxLength_EnforcesLimit()
    {
        SecureInputValidatorHelper.ValidateInput("abcdef", maxLength: 5).Should().BeFalse();
        SecureInputValidatorHelper.ValidateInput("abcde", maxLength: 5).Should().BeTrue();
    }

    [Theory]
    [InlineData("rm -rf ;")]
    [InlineData("echo | pipe")]
    [InlineData("cmd `whoami`")]
    [InlineData("var=$HOME")]
    [InlineData("cat > output")]
    [InlineData("cat < input")]
    public void ValidateInput_ShellMetaChars_ReturnsFalse(string input)
    {
        SecureInputValidatorHelper.ValidateInput(input).Should().BeFalse();
    }

    [Theory]
    [InlineData("cmd && evil")]
    [InlineData("cmd || fallback")]
    [InlineData("$(command)")]
    public void ValidateInput_ShellInjectionPatterns_ReturnsFalse(string input)
    {
        SecureInputValidatorHelper.ValidateInput(input).Should().BeFalse();
    }

    // --- ValidateFilePath ---

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("..\\windows\\system32")]
    [InlineData("some/%2e%2e/path")]
    [InlineData("some/%2f/path")]
    [InlineData("some/%5c/path")]
    public void ValidateFilePath_PathTraversal_ReturnsFalse(string path)
    {
        SecureInputValidatorHelper.ValidateFilePath(path).Should().BeFalse();
    }

    [Fact]
    public void ValidateFilePath_NullByte_ReturnsFalse()
    {
        SecureInputValidatorHelper.ValidateFilePath("file\0.txt").Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateFilePath_NullOrWhitespace_ReturnsFalse(string? path)
    {
        SecureInputValidatorHelper.ValidateFilePath(path!).Should().BeFalse();
    }

    [Fact]
    public void ValidateFilePath_ExceedsMaxPathLength_ReturnsFalse()
    {
        var longPath = new string('a', 4097);

        SecureInputValidatorHelper.ValidateFilePath(longPath).Should().BeFalse();
    }

    [Theory]
    [InlineData("C:/Users/docs/file.txt")]
    [InlineData("relative/path/to/file.cs")]
    public void ValidateFilePath_ValidPath_ReturnsTrue(string path)
    {
        SecureInputValidatorHelper.ValidateFilePath(path).Should().BeTrue();
    }

    // --- ValidateIdentifier ---

    [Theory]
    [InlineData("my-tool")]
    [InlineData("agent_v2")]
    [InlineData("mcp:server.name")]
    public void ValidateIdentifier_ValidIdentifier_ReturnsTrue(string id)
    {
        SecureInputValidatorHelper.ValidateIdentifier(id).Should().BeTrue();
    }

    [Theory]
    [InlineData("has spaces")]
    [InlineData("special@char")]
    [InlineData("path/slash")]
    public void ValidateIdentifier_InvalidChars_ReturnsFalse(string id)
    {
        SecureInputValidatorHelper.ValidateIdentifier(id).Should().BeFalse();
    }

    [Fact]
    public void ValidateIdentifier_ExceedsMaxLength_ReturnsFalse()
    {
        var longId = new string('a', 129);

        SecureInputValidatorHelper.ValidateIdentifier(longId).Should().BeFalse();
    }

    // --- Sanitize ---

    [Fact]
    public void Sanitize_RemovesShellMetaChars()
    {
        var result = SecureInputValidatorHelper.Sanitize("hello;world|test");

        result.Should().Be("helloworldtest");
    }

    [Fact]
    public void Sanitize_NullOrEmpty_ReturnsEmpty()
    {
        SecureInputValidatorHelper.Sanitize(null!).Should().BeEmpty();
        SecureInputValidatorHelper.Sanitize("").Should().BeEmpty();
    }

    [Fact]
    public void Sanitize_TruncatesToMaxLength()
    {
        var result = SecureInputValidatorHelper.Sanitize("abcdef", maxLength: 3);

        result.Should().Be("abc");
    }

    [Fact]
    public void Sanitize_RemovesControlChars()
    {
        var result = SecureInputValidatorHelper.Sanitize("hello\x01world");

        result.Should().Be("helloworld");
    }
}
