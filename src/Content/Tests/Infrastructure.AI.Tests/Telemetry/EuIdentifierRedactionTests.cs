using Domain.AI.Telemetry.Redaction;
using FluentAssertions;
using Infrastructure.AI.Telemetry.Redaction;
using Xunit;

namespace Infrastructure.AI.Tests.Telemetry;

/// <summary>
/// The EU identifiers <see cref="DefaultContentRedactionFilter"/> recognises (#903): each is redacted when its check
/// digits hold, left alone when they do not, and tagged by what it is even when every category runs, because these rules
/// run before the broad digit rules.
/// </summary>
/// <remarks>Every valid sample here was computed from its scheme's check-digit rule, not copied from memory.</remarks>
public sealed class EuIdentifierRedactionTests
{
    private readonly DefaultContentRedactionFilter _filter = new();

    [Theory]
    [InlineData("DE89 3704 0044 0532 0130 00")]
    [InlineData("DE89370400440532013000")]
    [InlineData("GB82 WEST 1234 5698 7654 32")]
    [InlineData("FR14 2004 1010 0505 0001 3M02 606")]
    [InlineData("BE71 0961 2345 6769")]
    public void Redact_ValidIban_IsMasked(string iban)
    {
        var result = _filter.Redact($"pay to {iban} today", [RedactionCategory.Iban]);

        result.Should().Be("pay to [REDACTED:Iban] today");
    }

    [Theory]
    [InlineData("DE89 3704 0044 0532 0130 01")] // one digit off: the check digits fail
    [InlineData("SKU12 ABCD EFGH IJKL")] // shaped like one, not an IBAN
    public void Redact_IbanShapeWithoutValidCheckDigits_IsLeftAlone(string text)
    {
        _filter.Redact($"ref {text} end", [RedactionCategory.Iban]).Should().Be($"ref {text} end");
    }

    [Fact]
    public void Redact_IbanFollowedByACapitalisedWord_MasksTheIbanAndKeepsTheWord()
    {
        // The spaced groups can run into the next word; the IBAN inside the match must still be found.
        var result = _filter.Redact("transfer DE89 3704 0044 0532 0130 00 AND confirm", [RedactionCategory.Iban]);

        result.Should().Be("transfer [REDACTED:Iban] AND confirm");
    }

    [Fact]
    public void Redact_AnIbanThatFailsItsCheckDigits_IsNotPartlyRedactedThroughAPrefixThatPassesByChance()
    {
        // DE83370400444348401964 fails mod 97, but a shorter prefix of it passes by chance: redacting that prefix would
        // leave the last digits in clear. A prefix counts only where an IBAN can end.
        _filter.Redact("ref DE83370400444348401964 end", [RedactionCategory.Iban])
            .Should().Be("ref DE83370400444348401964 end");
    }

    [Fact]
    public void Redact_ALookAlikeTokenBeforeAnIban_DoesNotHideTheIban()
    {
        var result = _filter.Redact("REF AB12 DE89 3704 0044 0532 0130 00", [RedactionCategory.Iban]);

        result.Should().Be("REF AB12 [REDACTED:Iban]");
    }

    [Fact]
    public void Redact_TwoIbansInARow_AreBothRedacted()
    {
        var result = _filter.Redact("DE89 3704 0044 0532 0130 00 GB82 WEST 1234 5698 7654 32", [RedactionCategory.Iban]);

        result.Should().Be("[REDACTED:Iban] [REDACTED:Iban]");
    }

    [Theory]
    [InlineData("KRSMRA85T10A562S")] // Kelvin sign, which case-insensitive [A-Z] would accept
    [InlineData("XK234567L")]
    [InlineData("KE89 3704 0044 0532 0130 00")]
    public void Redact_TextWithTheKelvinSign_NeverThrows(string text)
    {
        // Content reaching the filter can come from an MCP server or a user. A throw here fails the redaction on every
        // path (spans, logs, spills, audit), so a look-alike character must simply not match.
        var act = () => _filter.Redact($"value {text} end", Enum.GetValues<RedactionCategory>());

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("rssmra85t10a562s@pec.example.it")] // an Italian PEC mailbox built from a codice fiscale
    [InlineData("maria.12345678Z@corp.example.es")]
    public void Redact_AnEmailWhoseMailboxIsAnId_IsRedactedWhole(string email)
    {
        var result = _filter.Redact($"write to {email} today", Enum.GetValues<RedactionCategory>());

        result.Should().Be("write to [REDACTED:Email] today", "an id redacted first would break the address and leak the rest");
    }

    [Theory]
    [InlineData("12345678Z")] // Spanish DNI
    [InlineData("12345678-Z")]
    [InlineData("X1234567L")] // Spanish NIE
    [InlineData("RSSMRA85T10A562S")] // Italian codice fiscale
    [InlineData("1 85 05 78 006 084 91")] // French NIR, spaced
    [InlineData("1850578006084 91")]
    [InlineData("85.07.30-033.28")] // Belgian national number, born before 2000
    [InlineData("01.02.03-004.67")] // born from 2000
    public void Redact_ValidEuNationalId_IsMasked(string id)
    {
        var result = _filter.Redact($"id {id} on file", [RedactionCategory.EuNationalId]);

        result.Should().Be("id [REDACTED:EuNationalId] on file");
    }

    [Theory]
    [InlineData("12345678A")] // wrong DNI letter
    [InlineData("X1234567A")] // wrong NIE letter
    [InlineData("RSSMRA85T10A562A")] // wrong check character
    [InlineData("1 85 05 78 006 084 36")] // wrong NIR key
    [InlineData("85.07.30-033.29")] // wrong Belgian check digits
    public void Redact_EuNationalIdShapeWithWrongCheckDigits_IsLeftAlone(string text)
    {
        _filter.Redact($"id {text} on file", [RedactionCategory.EuNationalId]).Should().Be($"id {text} on file");
    }

    [Theory]
    [InlineData("DE89 3704 0044 0532 0130 00", "[REDACTED:Iban]")]
    [InlineData("1 85 05 78 006 084 91", "[REDACTED:EuNationalId]")]
    [InlineData("85.07.30-033.28", "[REDACTED:EuNationalId]")]
    public void Redact_EveryCategory_TagsTheIdentifierByWhatItIsNotAsAPhoneOrCard(string id, string tag)
    {
        var result = _filter.Redact($"value {id} end", Enum.GetValues<RedactionCategory>());

        result.Should().Be($"value {tag} end", "the check-digit rules run before the broad digit rules");
    }
}
