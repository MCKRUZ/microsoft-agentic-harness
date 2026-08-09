using Domain.AI.Iac;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Iac;

/// <summary>
/// Tests for <see cref="IacScanSeverityParser"/>, which decides both what a valid configured
/// blocking severity is and what severity a scanner finding carries.
/// </summary>
/// <remarks>
/// The type had no tests before #312, despite deciding whether an infrastructure scan blocks a
/// deployment. It hand-rolled two of <c>EnumNameHelper</c>'s four guards; the refusal cases below
/// are the two it used to accept.
/// <para>
/// The two entry points have deliberately opposite failure directions, which is the property most
/// worth pinning here: refusing a configured threshold fails closed, while refusing a scanner's
/// reported severity would fail open, so that path resolves to Critical instead.
/// </para>
/// </remarks>
public sealed class IacScanSeverityParserTests
{
    [Theory]
    [InlineData("Low", IacScanSeverity.Low)]
    [InlineData("high", IacScanSeverity.High)]
    [InlineData("CRITICAL", IacScanSeverity.Critical)]
    [InlineData("  Medium  ", IacScanSeverity.Medium)]
    public void TryParse_NamedSeverity_Parses(string value, IacScanSeverity expected)
    {
        IacScanSeverityParser.TryParse(value, out var severity).Should().BeTrue();
        severity.Should().Be(expected);
    }

    /// <summary>
    /// The numeric form, which used to parse: <c>"2"</c> resolved to <see cref="IacScanSeverity.High"/>.
    /// The problem is not that the two readers of a configured threshold disagreed — they share this
    /// method, so they never did — it is that a name and an opaque number were interchangeable, and
    /// <c>"99"</c> produced a severity no comparison could ever match. The severity regexes in the
    /// Checkov and tfsec parsers capture <c>\w+</c>, which includes digits, so the numeric form was
    /// reachable from scanner output as well as from config.
    /// </summary>
    [Theory]
    [InlineData("2")]
    [InlineData(" 2")]
    [InlineData("99")]
    [InlineData("-1")]
    [InlineData("+3")]
    public void TryParse_NumericForm_IsRefused(string value)
    {
        IacScanSeverityParser.TryParse(value, out _).Should().BeFalse();
    }

    /// <summary>
    /// A comma reads as a bitwise OR, and when the OR lands on a defined member
    /// <c>Enum.IsDefined</c> cannot tell it apart from having named that member — which is why the
    /// old two-guard implementation accepted it.
    /// </summary>
    [Theory]
    [InlineData("Low,Critical")]
    [InlineData("Low, High")]
    public void TryParse_CommaComposite_IsRefused(string value)
    {
        IacScanSeverityParser.TryParse(value, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Severe")]
    public void TryParse_BlankOrUnknown_IsRefused(string? value)
    {
        IacScanSeverityParser.TryParse(value, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("Critical", IacScanSeverity.Critical)]
    [InlineData("low", IacScanSeverity.Low)]
    public void ParseFindingSeverity_NamedSeverity_ReadsIt(string value, IacScanSeverity expected)
    {
        IacScanSeverityParser.ParseFindingSeverity(value).Should().Be(expected);
    }

    /// <summary>
    /// The finding path fails closed. Before #312 an unreadable severity left the tfsec parser's
    /// severity null, and its flush dropped the whole finding; Checkov and ARM-TTK kept their Medium
    /// default. Either way a scanner reporting something the harness could not read made a scan that
    /// should have blocked pass quietly.
    /// </summary>
    [Theory]
    [InlineData("3")]
    [InlineData("99")]
    [InlineData("Low,Critical")]
    [InlineData("banana")]
    [InlineData("")]
    [InlineData(null)]
    public void ParseFindingSeverity_UnreadableSeverity_IsCritical(string? value)
    {
        IacScanSeverityParser.ParseFindingSeverity(value).Should().Be(
            IacScanSeverity.Critical,
            "a security scanner saying something we cannot read must not lower the gate");
    }

    /// <summary>
    /// The property that makes the fallback safe: Critical is the top of the scale, so a finding
    /// carrying it blocks at every configured threshold.
    /// </summary>
    [Theory]
    [InlineData(IacScanSeverity.Low)]
    [InlineData(IacScanSeverity.Medium)]
    [InlineData(IacScanSeverity.High)]
    [InlineData(IacScanSeverity.Critical)]
    public void Passes_UnreadableFindingSeverity_BlocksAtEveryThreshold(IacScanSeverity blocking)
    {
        var finding = new IacScanFinding
        {
            Scanner = "tfsec",
            RuleId = "rule-1",
            Severity = IacScanSeverityParser.ParseFindingSeverity("3"),
            Message = "unreadable severity"
        };

        IacScanSeverityParser.Passes([finding], blocking).Should().BeFalse();
    }
}
