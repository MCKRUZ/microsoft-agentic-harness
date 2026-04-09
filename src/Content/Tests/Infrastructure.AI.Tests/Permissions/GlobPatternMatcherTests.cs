using FluentAssertions;
using Infrastructure.AI.Permissions;
using Xunit;

namespace Infrastructure.AI.Tests.Permissions;

public sealed class GlobPatternMatcherTests
{
    private readonly GlobPatternMatcher _matcher = new();

    [Fact]
    public void ExactMatch_ReturnsTrue()
    {
        _matcher.IsMatch("file_system", "file_system").Should().BeTrue();
    }

    [Fact]
    public void ExactMatch_ReturnsFalse()
    {
        _matcher.IsMatch("file_system", "web_fetch").Should().BeFalse();
    }

    [Theory]
    [InlineData("git:*", "git:push")]
    [InlineData("git:*", "git:commit")]
    [InlineData("bash:*", "bash:exec")]
    public void PrefixWildcard_MatchesSubcommands(string pattern, string value)
    {
        _matcher.IsMatch(pattern, value).Should().BeTrue();
    }

    [Theory]
    [InlineData("git:*", "git")]
    [InlineData("git:*", "github:push")]
    public void PrefixWildcard_DoesNotMatchNonSubcommands(string pattern, string value)
    {
        _matcher.IsMatch(pattern, value).Should().BeFalse();
    }

    [Theory]
    [InlineData("*", "anything")]
    [InlineData("*", "file_system")]
    [InlineData("*", "bash:exec")]
    public void Wildcard_MatchesEverything(string pattern, string value)
    {
        _matcher.IsMatch(pattern, value).Should().BeTrue();
    }

    [Fact]
    public void EmptyPattern_MatchesNothing()
    {
        _matcher.IsMatch("", "file_system").Should().BeFalse();
        _matcher.IsMatch("", "").Should().BeFalse();
    }

    [Theory]
    [InlineData("File_System", "file_system")]
    [InlineData("file_system", "File_System")]
    [InlineData("GIT:*", "git:push")]
    public void CaseInsensitive_Matching(string pattern, string value)
    {
        _matcher.IsMatch(pattern, value).Should().BeTrue();
    }

    [Fact]
    public void NullOrEmptyValue_ReturnsFalse()
    {
        _matcher.IsMatch("file_system", "").Should().BeFalse();
    }
}
