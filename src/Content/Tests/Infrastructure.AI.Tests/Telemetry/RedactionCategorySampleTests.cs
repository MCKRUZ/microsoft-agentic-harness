using Domain.AI.Telemetry.Redaction;
using FluentAssertions;
using Infrastructure.AI.Telemetry.Redaction;
using Xunit;

namespace Infrastructure.AI.Tests.Telemetry;

/// <summary>
/// Every <see cref="RedactionCategory"/> has a sample its rule must redact and a near miss it must leave alone (#903). A
/// category added to the enum without both fails here, so a new category can never ship as a name with no working rule
/// behind it.
/// </summary>
public sealed class RedactionCategorySampleTests
{
    private readonly DefaultContentRedactionFilter _filter = new();

    /// <summary>For each category: a value its rule must redact, the tag it leaves, and a near miss it must not touch.</summary>
    private static readonly IReadOnlyDictionary<RedactionCategory, (string Positive, string Tag, string Negative)> Samples =
        new Dictionary<RedactionCategory, (string, string, string)>
        {
            [RedactionCategory.Email] = ("alice@example.com", "[REDACTED:Email]", "alice at example dot com"),
            [RedactionCategory.Phone] = ("+1 415 555 0123", "[REDACTED:Phone]", "call the front desk"),
            [RedactionCategory.Ssn] = ("123-45-6789", "[REDACTED:Ssn]", "12-345"),
            [RedactionCategory.CreditCard] = ("4111 1111 1111 1111", "[REDACTED:CreditCard]", "4111 1111"),
            [RedactionCategory.IpAddress] = ("192.168.10.20", "[REDACTED:IpAddress]", "version 1.2.3"),
            [RedactionCategory.AwsKey] = ("AKIAIOSFODNN7EXAMPLE", "[REDACTED:AwsKey]", "AKIA1234"),
            [RedactionCategory.JwtToken] = ("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJl", "[REDACTED:JwtToken]", "eyJhbGciOiJIUzI1NiJ9"),
            [RedactionCategory.Generic] = ("api_key=abc123secret", "[REDACTED]", "no api key is configured"),
            [RedactionCategory.VendorApiKey] = ("sk-abcdefghijklmnopqrstuvwxyz012345", "[REDACTED:VendorApiKey]", "sk-short"),
            [RedactionCategory.Iban] = ("DE89 3704 0044 0532 0130 00", "[REDACTED:Iban]", "DE89 3704 0044 0532 0130 01"),
            [RedactionCategory.EuNationalId] = ("12345678Z", "[REDACTED:EuNationalId]", "12345678A"),
        };

    [Fact]
    public void EveryCategory_HasSamples()
    {
        Samples.Keys.Should().BeEquivalentTo(Enum.GetValues<RedactionCategory>(),
            "a category added without a positive and a negative sample has no proof its rule works");
    }

    public static TheoryData<RedactionCategory> Categories() => [.. Enum.GetValues<RedactionCategory>()];

    [Theory]
    [MemberData(nameof(Categories))]
    public void EachCategory_RedactsItsSample_AndLeavesItsNearMissAlone(RedactionCategory category)
    {
        var (positive, tag, negative) = Samples[category];

        var redacted = _filter.Redact($"before {positive} after", [category]);
        redacted.Should().NotContain(positive.Split('=')[^1]).And.Contain(tag);
        _filter.Redact($"before {negative} after", [category]).Should().Be($"before {negative} after");
    }
}
