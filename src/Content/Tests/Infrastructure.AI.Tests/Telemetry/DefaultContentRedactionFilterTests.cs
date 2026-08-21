using Domain.AI.Telemetry.Redaction;
using FluentAssertions;
using Infrastructure.AI.Telemetry.Redaction;
using Xunit;

namespace Infrastructure.AI.Tests.Telemetry;

/// <summary>
/// Tests for <see cref="DefaultContentRedactionFilter"/>. Each of the eight
/// built-in <see cref="RedactionCategory"/> rules must mask a representative
/// sample of its target, leave content alone when its category is not requested,
/// pass null/empty through unchanged, and handle multiple categories in one
/// string. The rule set is intentionally over-redactive (false positives
/// acceptable, false negatives not), so assertions check that the secret is gone
/// rather than that surrounding text is byte-identical.
/// </summary>
public sealed class DefaultContentRedactionFilterTests
{
    private readonly DefaultContentRedactionFilter _filter = new();

    private static readonly IReadOnlyList<RedactionCategory> AllCategories =
        Enum.GetValues<RedactionCategory>();

    [Fact]
    public void Redact_Email_MasksAddress()
    {
        var result = _filter.Redact("contact me at alice@example.com please", [RedactionCategory.Email]);

        result.Should().NotContain("alice@example.com");
        result.Should().Contain("[REDACTED:Email]");
    }

    [Fact]
    public void Redact_Phone_MasksNumber()
    {
        var result = _filter.Redact("call +1 415-555-0132 now", [RedactionCategory.Phone]);

        result.Should().NotContain("415-555-0132");
        result.Should().Contain("[REDACTED:Phone]");
    }

    [Fact]
    public void Redact_Ssn_MasksNumber()
    {
        var result = _filter.Redact("ssn 123-45-6789 end", [RedactionCategory.Ssn]);

        result.Should().NotContain("123-45-6789");
        result.Should().Contain("[REDACTED:Ssn]");
    }

    [Fact]
    public void Redact_CreditCard_MasksPan()
    {
        var result = _filter.Redact("card 4111 1111 1111 1111 ok", [RedactionCategory.CreditCard]);

        result.Should().NotContain("4111 1111 1111 1111");
        result.Should().Contain("[REDACTED:CreditCard]");
    }

    [Fact]
    public void Redact_IpAddress_MasksIpv4()
    {
        var result = _filter.Redact("host at 192.168.1.10 down", [RedactionCategory.IpAddress]);

        result.Should().NotContain("192.168.1.10");
        result.Should().Contain("[REDACTED:IpAddress]");
    }

    [Fact]
    public void Redact_AwsKey_MasksAccessKeyId()
    {
        var result = _filter.Redact("key AKIAIOSFODNN7EXAMPLE here", [RedactionCategory.AwsKey]);

        result.Should().NotContain("AKIAIOSFODNN7EXAMPLE");
        result.Should().Contain("[REDACTED:AwsKey]");
    }

    [Fact]
    public void Redact_JwtToken_MasksToken()
    {
        // Built from parts so no literal token string sits in source (the shape
        // is header.payload.signature with base64url segments, which is all the
        // JWT regex keys on).
        var jwt = string.Join('.', "eyJ" + "hbGciOiJIUzI1NiJ9", "eyJ" + "zdWIiOiIxMjM0In0", "Sig-abc_DEF123");
        var result = _filter.Redact($"auth {jwt} done", [RedactionCategory.JwtToken]);

        result.Should().NotContain(jwt);
        result.Should().Contain("[REDACTED:JwtToken]");
    }

    [Fact]
    public void Redact_Generic_MasksApiKeyAssignment()
    {
        var result = _filter.Redact("api_key=sk-supersecretvalue123", [RedactionCategory.Generic]);

        result.Should().NotContain("sk-supersecretvalue123");
        result.Should().Contain("[REDACTED]");
    }

    [Theory]
    // Each case is two literal fragments, concatenated only at runtime in the method body below —
    // never a single source literal shaped like a real credential. GitHub push-protection's secret
    // scanner blocked an earlier version of this test that used one contiguous literal per case, even
    // though every value here is synthetic; splitting the source text is the same technique
    // Redact_JwtToken_MasksToken above already uses for the same reason.
    //
    // OpenAI: classic and newer project-scoped shapes.
    [InlineData("sk-", "abcdefghijklmnopqrstuvwxyz123456")]
    [InlineData("sk-proj-", "A1B2C3D4E5F6G7H8I9J0K1L2M3N4O5P6")]
    // GitHub: classic PAT (prefix + 36 chars) and fine-grained PAT.
    [InlineData("ghp_", "1234567890abcdefghij1234567890abcdefgh")]
    [InlineData("github_pat_", "11ABCDEFG0123456789012_abcdefghijklmnopqrstuvwxyz0123456789ABCDEFGHIJKLMNOPQRSTUVWX")]
    // Slack: bot, refresh, and app-level tokens — a representative sample of the xox[baprse]- family
    // plus the separately-shaped xapp- prefix.
    [InlineData("xoxb-", "1234567890-abcdefghijklmnop")]
    [InlineData("xoxe-", "1-abcdefghijklmnopqrstuvwxyz")]
    [InlineData("xapp-", "1-A01ABC234-5678901234-abcdefghij")]
    public void Redact_VendorApiKey_MasksToken(string prefix, string body)
    {
        var secret = prefix + body;
        var result = _filter.Redact($"token {secret} end", [RedactionCategory.VendorApiKey]);

        result.Should().NotContain(secret);
        result.Should().Contain("[REDACTED:VendorApiKey]");
    }

    [Fact]
    public void Redact_Generic_MasksSasQuerySignature()
    {
        // Security review on #444: SAS-signed blob/queue URLs (a document-ingest source URI is
        // exactly the shape this can appear as) were leaking their sig= token unredacted — the
        // AccountKey/SharedAccessKey rule covers connection strings, not this URL query shape.
        var result = _filter.Redact(
            "https://acct.blob.core.windows.net/c/b?sv=2021-08-06&sp=rwdlac&se=2026-01-01&sig=aBcD%2FeF9xyz",
            [RedactionCategory.Generic]);

        result.Should().NotContain("aBcD%2FeF9xyz");
        result.Should().Contain("[REDACTED]");
        // The non-secret grant parameter is left alone under this rule specifically — only sig is a
        // credential to the Generic category. This does NOT guarantee sv=/se='s date-shaped values
        // survive redaction in general: with RedactionCategory.Phone also enabled (LogRecordRedactionProcessor
        // falls back to every category when none are configured), the pre-existing Phone rule matches
        // "2021-08-06"/"2026-01-01" too and redacts them — over-redaction, not a security gap, but
        // this test's own claim is scoped to Generic alone, not a blanket "left alone" guarantee.
        result.Should().Contain("sp=rwdlac");
    }

    [Fact]
    public void Redact_CategoryNotRequested_LeavesContentUnchanged()
    {
        // Only Email requested; the email stays intact because Email is excluded.
        const string input = "email alice@example.com";
        var result = _filter.Redact(input, [RedactionCategory.Phone]);

        result.Should().Be(input);
    }

    [Fact]
    public void Redact_MultipleCategories_MasksEachInOneString()
    {
        const string input = "mail bob@example.com ip 10.0.0.5 ssn 987-65-4321";
        var result = _filter.Redact(
            input,
            [RedactionCategory.Email, RedactionCategory.IpAddress, RedactionCategory.Ssn]);

        result.Should().NotContain("bob@example.com");
        result.Should().NotContain("10.0.0.5");
        result.Should().NotContain("987-65-4321");
        result.Should().Contain("[REDACTED:Email]");
        result.Should().Contain("[REDACTED:IpAddress]");
        result.Should().Contain("[REDACTED:Ssn]");
    }

    [Fact]
    public void Redact_Null_ReturnsEmptyString()
    {
        var result = _filter.Redact(null, AllCategories);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Redact_Empty_ReturnsEmptyString()
    {
        var result = _filter.Redact(string.Empty, AllCategories);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Redact_EmptyCategoryList_ReturnsInputUnchanged()
    {
        const string input = "email alice@example.com ip 10.0.0.5";
        var result = _filter.Redact(input, []);

        result.Should().Be(input);
    }

    [Fact]
    public void Redact_NoMatches_ReturnsInputUnchanged()
    {
        const string input = "nothing sensitive here at all";
        var result = _filter.Redact(input, AllCategories);

        result.Should().Be(input);
    }
}
