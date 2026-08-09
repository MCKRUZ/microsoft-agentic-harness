using Domain.Common.Helpers;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Helpers;

/// <summary>
/// Tests for <see cref="EnumNameHelper"/>.
/// </summary>
/// <remarks>
/// <para>
/// The whole point of this helper is the gap between "parsing succeeded" and "the result is a real
/// member". <see cref="Enum.TryParse{TEnum}(string?, bool, out TEnum)"/> returns <c>true</c> for any
/// integer string, including one outside the defined range, and hands back a value that compiles,
/// compares, and prints — while being a member that does not exist.
/// </para>
/// <para>
/// <see cref="TryParseName_UndefinedNumericValue_IsRejected"/> is the test that matters: it is the
/// case that shipped as a real defect (#296), where a governance threshold parsed to an undefined
/// value and silently disabled the comparison it existed to drive.
/// </para>
/// </remarks>
public class EnumNameHelperTests
{
    /// <summary>
    /// A stand-in with a small defined range, so "outside the range" is testable. Public because a
    /// <c>[Theory]</c> parameter cannot be less accessible than the test method.
    /// </summary>
    public enum Severity
    {
        Low = 0,
        Medium = 1,
        High = 2
    }

    [Theory]
    [InlineData("Low", Severity.Low)]
    [InlineData("Medium", Severity.Medium)]
    [InlineData("High", Severity.High)]
    public void TryParseName_DefinedMemberName_Parses(string value, Severity expected)
    {
        EnumNameHelper.TryParseName<Severity>(value, out var parsed).Should().BeTrue();
        parsed.Should().Be(expected);
    }

    [Theory]
    [InlineData("low")]
    [InlineData("LOW")]
    [InlineData("LoW")]
    public void TryParseName_IsCaseInsensitive(string value)
    {
        // Configuration is hand-written, so casing must not be a trap. The rejection is about
        // numeric forms, not about being fussy.
        EnumNameHelper.TryParseName<Severity>(value, out var parsed).Should().BeTrue();
        parsed.Should().Be(Severity.Low);
    }

    [Fact]
    public void TryParseName_UndefinedNumericValue_IsRejected()
    {
        // THE regression case. Enum.TryParse succeeds here and yields Severity=99 — not a member.
        // Downstream, a comparison like `value >= threshold` then silently stops ever being true.
        EnumNameHelper.TryParseName<Severity>("99", out var parsed).Should().BeFalse();
        parsed.Should().Be(default(Severity));

        // Proof the guard is load-bearing rather than decorative: the framework call this replaces
        // accepts the same input and produces the undefined value.
        Enum.TryParse<Severity>("99", ignoreCase: true, out var viaFramework).Should().BeTrue();
        Enum.IsDefined(viaFramework).Should().BeFalse();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("2")]
    public void TryParseName_NumericFormOfADefinedMember_IsStillRejected(string value)
    {
        // Rejected even though the number does name a real member. The contract is names only, so
        // that configuration reads the same everywhere and a value cannot mean one thing at boot
        // and another at runtime — which is exactly what #296 was.
        EnumNameHelper.TryParseName<Severity>(value, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("+1")]
    public void TryParseName_SignedNumericValue_IsRejected(string value)
    {
        EnumNameHelper.TryParseName<Severity>(value, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(" 2")]
    [InlineData("2 ")]
    [InlineData("\t2")]
    [InlineData(" -1")]
    public void TryParseName_NumericValuePaddedWithWhitespace_IsStillRejected(string value)
    {
        // Found by this test failing on its first run (#300). The leading-character guard inspects
        // value[0], so a single leading space is not a digit and the guard waves the value through —
        // and Enum.TryParse trims before parsing, so " 2" then parses as the numeric form of High.
        // Configuration files carry stray whitespace routinely, so this is not a contrived input: it
        // is the numeric-form hole the helper exists to close, reachable with one space.
        EnumNameHelper.TryParseName<Severity>(value, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(" Low")]
    [InlineData("Low ")]
    [InlineData("  Low  ")]
    public void TryParseName_NamePaddedWithWhitespace_IsAccepted(string value)
    {
        // The whitespace rejection above must be about the numeric form, not about being fussy with
        // hand-edited config. A padded member NAME still parses.
        EnumNameHelper.TryParseName<Severity>(value, out var parsed).Should().BeTrue();
        parsed.Should().Be(Severity.Low);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParseName_NullOrBlank_IsRejected(string? value)
    {
        EnumNameHelper.TryParseName<Severity>(value, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParseName_NameThatIsNotAMember_IsRejected()
    {
        EnumNameHelper.TryParseName<Severity>("Catastrophic", out _).Should().BeFalse();
    }

    [Fact]
    public void TryParseName_CommaSeparatedNames_AreRejectedEvenWhenTheCombinationIsDefined()
    {
        // Found by this test failing on its first run, which made it a finding rather than a
        // formality. Enum.TryParse reads a comma list as a bitwise OR: Low(0) | High(2) is 2, which
        // IS a defined member — so Enum.IsDefined cannot catch it, and "Low,High" was silently
        // accepted as "High". A comma check is the only thing that refuses it.
        EnumNameHelper.TryParseName<Severity>("Low,High", out _).Should().BeFalse();

        // The framework call this replaces accepts it and yields a value indistinguishable from
        // having named High directly — which is precisely why IsDefined is not sufficient alone.
        Enum.TryParse<Severity>("Low,High", ignoreCase: true, out var viaFramework).Should().BeTrue();
        viaFramework.Should().Be(Severity.High);
        Enum.IsDefined(viaFramework).Should().BeTrue();
    }

    [Fact]
    public void TryParseName_Rejection_LeavesTheOutParameterAtDefault()
    {
        // Callers use this as `TryParseName(...) ? parsed : Fallback`. A rejection must never leave a
        // half-parsed value behind for a caller that ignores the bool.
        EnumNameHelper.TryParseName<Severity>("nonsense", out var parsed).Should().BeFalse();
        parsed.Should().Be(default(Severity));
    }
}
