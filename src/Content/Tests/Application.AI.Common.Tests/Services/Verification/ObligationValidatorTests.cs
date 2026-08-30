using Application.AI.Common.Services.Verification;
using Domain.AI.Verification;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Services.Verification;

public sealed class ObligationValidatorTests
{
    private readonly ObligationValidator _sut = new();

    [Fact]
    public void Validate_WellFormedObligation_ReturnsValid()
    {
        var obligation = new Obligation(Where: "line 10 imports Foo", ReliesOn: "class Foo declaration", Property: "Foo exists");

        var result = _sut.Validate(obligation);

        result.IsValid.Should().BeTrue();
        result.RejectionReason.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptyOrWhitespaceReliesOn_RejectsAsEmptyReliesOn(string reliesOn)
    {
        var obligation = new Obligation(Where: "where", ReliesOn: reliesOn, Property: "property");

        var result = _sut.Validate(obligation);

        result.IsValid.Should().BeFalse();
        result.RejectionReason.Should().Be(ObligationRejectionReason.EmptyReliesOn);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptyOrWhitespaceProperty_RejectsAsEmptyProperty(string property)
    {
        var obligation = new Obligation(Where: "where", ReliesOn: "relies on", Property: property);

        var result = _sut.Validate(obligation);

        result.IsValid.Should().BeFalse();
        result.RejectionReason.Should().Be(ObligationRejectionReason.EmptyProperty);
    }

    [Fact]
    public void Validate_ReliesOnExactlyEqualsWhere_RejectsAsReliesOnEqualsWhere()
    {
        var obligation = new Obligation(Where: "the same text", ReliesOn: "the same text", Property: "property");

        var result = _sut.Validate(obligation);

        result.IsValid.Should().BeFalse();
        result.RejectionReason.Should().Be(ObligationRejectionReason.ReliesOnEqualsWhere);
    }

    // Proves the comparison is normalized, not a bare string ==. If ObligationValidator ever
    // regressed to a plain equality check, this obligation (same text, differing only in
    // HTML-entity encoding and whitespace) would wrongly pass as valid.
    [Fact]
    public void Validate_ReliesOnEqualsWhereAfterNormalization_RejectsAsReliesOnEqualsWhere()
    {
        var obligation = new Obligation(
            Where: "the &lt;same&gt;   text",
            ReliesOn: "the <same>\ntext",
            Property: "property");

        var result = _sut.Validate(obligation);

        result.IsValid.Should().BeFalse();
        result.RejectionReason.Should().Be(ObligationRejectionReason.ReliesOnEqualsWhere);
    }

    [Fact]
    public void Validate_NullObligation_Throws()
    {
        var act = () => _sut.Validate(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
