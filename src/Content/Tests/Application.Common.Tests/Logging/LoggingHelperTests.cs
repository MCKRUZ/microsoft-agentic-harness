using Application.Common.Logging;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Application.Common.Tests.Logging;

public sealed class LoggingHelperTests
{
    // -- GetShortLevel --

    [Theory]
    [InlineData(LogLevel.Trace, "TRCE")]
    [InlineData(LogLevel.Debug, "DBUG")]
    [InlineData(LogLevel.Information, "INFO")]
    [InlineData(LogLevel.Warning, "WARN")]
    [InlineData(LogLevel.Error, "ERR ")]
    [InlineData(LogLevel.Critical, "CRIT")]
    [InlineData(LogLevel.None, "UNKN")]
    public void GetShortLevel_ReturnsCorrectAbbreviation(LogLevel level, string expected)
    {
        LoggingHelper.GetShortLevel(level).Should().Be(expected);
    }

    // -- GetLevelName --

    [Theory]
    [InlineData(LogLevel.Trace, "trace")]
    [InlineData(LogLevel.Debug, "debug")]
    [InlineData(LogLevel.Information, "info")]
    [InlineData(LogLevel.Warning, "warn")]
    [InlineData(LogLevel.Error, "error")]
    [InlineData(LogLevel.Critical, "critical")]
    [InlineData(LogLevel.None, "unknown")]
    public void GetLevelName_ReturnsLowercaseName(LogLevel level, string expected)
    {
        LoggingHelper.GetLevelName(level).Should().Be(expected);
    }

    // -- GetShortCategory --

    [Theory]
    [InlineData("Application.Core.Services.MyService", "MyService")]
    [InlineData("MyService", "MyService")]
    [InlineData("A.B.C.D.E", "E")]
    [InlineData("", "")]
    public void GetShortCategory_ExtractsLastSegment(string category, string expected)
    {
        LoggingHelper.GetShortCategory(category).Should().Be(expected);
    }

    // -- GetExecutorDisplayName --

    [Fact]
    public void GetExecutorDisplayName_NullParent_ReturnsExecutorIdOnly()
    {
        LoggingHelper.GetExecutorDisplayName("planner", null)
            .Should().Be("planner");
    }

    [Fact]
    public void GetExecutorDisplayName_WithParent_ReturnsParentChildNotation()
    {
        LoggingHelper.GetExecutorDisplayName("research", "main")
            .Should().Be("main>research");
    }

    // -- FormatCompactCount --

    [Theory]
    [InlineData(0, "0")]
    [InlineData(128, "128")]
    [InlineData(999, "999")]
    [InlineData(1500, "1.5k")]
    [InlineData(9999, "10.0k")]
    [InlineData(15300, "15k")]
    [InlineData(1500000, "1.5M")]
    public void FormatCompactCount_FormatsCorrectly(long count, string expected)
    {
        LoggingHelper.FormatCompactCount(count).Should().Be(expected);
    }

    // -- FormatCompactDuration --

    [Fact]
    public void FormatCompactDuration_SubMillisecond_ReturnsLessThanOne()
    {
        LoggingHelper.FormatCompactDuration(TimeSpan.FromMicroseconds(500))
            .Should().Be("<1ms");
    }

    [Fact]
    public void FormatCompactDuration_Milliseconds_ReturnsMs()
    {
        LoggingHelper.FormatCompactDuration(TimeSpan.FromMilliseconds(45))
            .Should().Be("45ms");
    }

    [Fact]
    public void FormatCompactDuration_Seconds_ReturnsSecondsWithDecimal()
    {
        LoggingHelper.FormatCompactDuration(TimeSpan.FromSeconds(1.2))
            .Should().Be("1.2s");
    }

    [Fact]
    public void FormatCompactDuration_Minutes_ReturnsMinutesAndSeconds()
    {
        LoggingHelper.FormatCompactDuration(TimeSpan.FromSeconds(123))
            .Should().Be("2m03s");
    }

    // -- SanitizeForLog --

    [Fact]
    public void SanitizeForLog_Null_ReturnsEmpty()
    {
        LoggingHelper.SanitizeForLog(null).Should().BeEmpty();
    }

    [Fact]
    public void SanitizeForLog_Empty_ReturnsEmpty()
    {
        LoggingHelper.SanitizeForLog("").Should().BeEmpty();
    }

    [Fact]
    public void SanitizeForLog_ShortCleanString_ReturnedUnchanged()
    {
        LoggingHelper.SanitizeForLog("Hello World")
            .Should().Be("Hello World");
    }

    [Fact]
    public void SanitizeForLog_ControlCharacters_ReplacedWithSpace()
    {
        // Tab (\x09) and newline (\x0A) are control characters in the [\x00-\x1F] range.
        var result = LoggingHelper.SanitizeForLog("Hello\tWorld\nEnd");

        result.Should().Be("Hello World End");
    }

    [Fact]
    public void SanitizeForLog_LongString_Truncated()
    {
        var longString = new string('A', 300);

        var result = LoggingHelper.SanitizeForLog(longString, maxLength: 100);

        result.Should().HaveLength(100 + "...[truncated]".Length);
        result.Should().EndWith("...[truncated]");
    }

    [Fact]
    public void SanitizeForLog_ExactlyMaxLength_NotTruncated()
    {
        var exact = new string('B', 200);

        var result = LoggingHelper.SanitizeForLog(exact);

        result.Should().Be(exact);
        result.Should().NotContain("[truncated]");
    }

    [Fact]
    public void SanitizeForLog_NoControlChars_SkipsRegex()
    {
        // Pure ASCII printable — fast path should skip regex
        var clean = "Just normal text with spaces and numbers 12345";

        var result = LoggingHelper.SanitizeForLog(clean);

        result.Should().Be(clean);
    }

    // -- ExtractMessageFromFormatted --

    [Fact]
    public void ExtractMessageFromFormatted_NoPrefixPattern_ReturnsOriginal()
    {
        LoggingHelper.ExtractMessageFromFormatted("Just a message")
            .Should().Be("Just a message");
    }

    // -- GetStableExecutorColor --

    [Fact]
    public void GetStableExecutorColor_SameId_ReturnsSameColor()
    {
        var color1 = LoggingHelper.GetStableExecutorColor("planner");
        var color2 = LoggingHelper.GetStableExecutorColor("planner");

        color1.Should().Be(color2);
    }

    [Fact]
    public void GetStableExecutorColor_DifferentIds_MayReturnDifferentColors()
    {
        var color = LoggingHelper.GetStableExecutorColor("test-executor");

        color.Should().StartWith("\x1B[");
    }

    // -- GetLevelColor --

    [Theory]
    [InlineData(LogLevel.Trace)]
    [InlineData(LogLevel.Debug)]
    [InlineData(LogLevel.Information)]
    [InlineData(LogLevel.Warning)]
    [InlineData(LogLevel.Error)]
    [InlineData(LogLevel.Critical)]
    public void GetLevelColor_AllLevels_ReturnAnsiEscapeSequence(LogLevel level)
    {
        LoggingHelper.GetLevelColor(level).Should().StartWith("\x1B[");
    }
}
