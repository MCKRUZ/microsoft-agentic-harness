using Domain.Common.Config.MetaHarness;
using FluentAssertions;
using Infrastructure.AI.Security;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Security;

public class PatternSecretRedactorTests
{
    private static PatternSecretRedactor CreateRedactor(
        IReadOnlyList<string>? denylist = null)
    {
        var config = new MetaHarnessConfig
        {
            SecretsRedactionPatterns = denylist
                ?? ["Key", "Secret", "Token", "Password", "ConnectionString"]
        };
        var monitor = new Mock<IOptionsMonitor<MetaHarnessConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(config);
        return new PatternSecretRedactor(monitor.Object);
    }

    /// <summary>
    /// A string containing "Authorization: Bearer eyABC123..." has the token value
    /// replaced with "[REDACTED]", leaving the "Bearer" prefix intact.
    /// </summary>
    [Fact]
    public void Redact_StringContainingBearerToken_ReplacesWithRedacted()
    {
        var sut = CreateRedactor();
        var input = "Authorization: Bearer eyABC123xyz==";

        var result = sut.Redact(input);

        result.Should().Be("Authorization: Bearer [REDACTED]");
    }

    /// <summary>
    /// A plain string with no secret patterns is returned exactly as-is
    /// (same reference or equal value, no mutation).
    /// </summary>
    [Fact]
    public void Redact_StringWithNoSecrets_ReturnsUnchanged()
    {
        var sut = CreateRedactor();
        var input = "The quick brown fox jumped over the lazy dog.";

        var result = sut.Redact(input);

        result.Should().Be(input);
    }

    /// <summary>
    /// A config key named "AzureOpenAIApiKey" matches the "Key" pattern and
    /// IsSecretKey returns true.
    /// </summary>
    [Fact]
    public void IsSecretKey_KeyMatchingDenylistPattern_ReturnsTrue()
    {
        var sut = CreateRedactor();

        sut.IsSecretKey("AzureOpenAIApiKey").Should().BeTrue();
    }

    /// <summary>
    /// A config key named "MaxIterations" does not match any denylist pattern
    /// and IsSecretKey returns false.
    /// </summary>
    [Fact]
    public void IsSecretKey_KeyNotMatchingAnyPattern_ReturnsFalse()
    {
        var sut = CreateRedactor();

        sut.IsSecretKey("MaxIterations").Should().BeFalse();
    }

    /// <summary>
    /// IsSecretKey matching is case-insensitive: "apikey" matches "Key".
    /// </summary>
    [Fact]
    public void IsSecretKey_CaseInsensitiveMatch_ReturnsTrue()
    {
        var sut = CreateRedactor();

        sut.IsSecretKey("apikey").Should().BeTrue();
    }

    /// <summary>
    /// Redact(null) returns null without throwing.
    /// Redact("") returns "" without throwing.
    /// </summary>
    [Fact]
    public void Redact_NullOrEmpty_ReturnsInputUnchanged()
    {
        var sut = CreateRedactor();

        sut.Redact(null).Should().BeNull();
        sut.Redact("").Should().Be("");
    }

    /// <summary>
    /// A connection string containing "AccountKey=abc123;" has the value portion
    /// replaced with "[REDACTED]".
    /// </summary>
    [Fact]
    public void Redact_ConnectionStringWithAccountKey_RedactsValue()
    {
        var sut = CreateRedactor();
        var input = "DefaultEndpointsProtocol=https;AccountKey=abc123secret;EndpointSuffix=core.windows.net";

        var result = sut.Redact(input);

        result.Should().Contain("AccountKey=[REDACTED]");
        result.Should().NotContain("abc123secret");
    }

    /// <summary>
    /// A string with multiple secret occurrences has all of them redacted,
    /// not just the first match.
    /// </summary>
    [Fact]
    public void Redact_MultipleSecretsInInput_RedactsAll()
    {
        var sut = CreateRedactor();
        var input = "Bearer tokenABC and api_key=superSecret123";

        var result = sut.Redact(input);

        result.Should().Contain("Bearer [REDACTED]");
        result.Should().Contain("[REDACTED]");
        result.Should().NotContain("tokenABC");
        result.Should().NotContain("superSecret123");
    }

    /// <summary>
    /// Calling Redact on an already-redacted string returns the same output (idempotent).
    /// The "[REDACTED]" placeholder must not itself match any pattern.
    /// </summary>
    [Fact]
    public void Redact_AlreadyRedactedString_ReturnsUnchanged()
    {
        var sut = CreateRedactor();
        var input = "AccountKey=[REDACTED]";

        var result = sut.Redact(input);

        result.Should().Be(input);
    }

    /// <summary>
    /// Direct-list constructor accepting IReadOnlyList&lt;string&gt; initializes correctly
    /// and uses the provided denylist.
    /// </summary>
    [Fact]
    public void DirectListConstructor_WithExplicitDenylist_UsesProvidedPatterns()
    {
        var sut = new PatternSecretRedactor(["password"]);

        sut.IsSecretKey("DbPassword").Should().BeTrue();
        sut.IsSecretKey("MaxIterations").Should().BeFalse();
    }

    /// <summary>
    /// A JSON-quoted secret key/value pair — the shape tool call arguments and results are routinely
    /// serialized as — is redacted. The pre-existing generic key=value/key:value pattern requires an
    /// unquoted value and never matches this shape, so a distinct pattern is required.
    /// </summary>
    [Fact]
    public void Redact_JsonQuotedApiKey_RedactsValue()
    {
        var sut = CreateRedactor();
        var input = """{"toolName":"http_call","api_key":"sk-superSecret123","timeout":30}""";

        var result = sut.Redact(input);

        result.Should().Contain("""api_key":"[REDACTED]""");
        result.Should().NotContain("sk-superSecret123");
    }

    /// <summary>
    /// Calling Redact twice on a JSON-quoted secret is idempotent — the "[REDACTED]" placeholder
    /// must not itself match the JSON-shaped pattern on a second pass.
    /// </summary>
    [Fact]
    public void Redact_AlreadyRedactedJsonQuotedSecret_ReturnsUnchanged()
    {
        var sut = CreateRedactor();
        var input = """{"password":"[REDACTED]"}""";

        var result = sut.Redact(input);

        result.Should().Be(input);
    }

    /// <summary>
    /// A JSON-quoted secret value containing an escaped quote (e.g. the actual value is <c>ab"cd</c>,
    /// serialized as <c>"ab\"cd"</c>) is redacted in full. A plain <c>[^"]*</c> value matcher stops at
    /// the escaped quote character regardless of the preceding backslash, truncating the match mid-value
    /// and leaking the remainder of the secret in plaintext while corrupting the surrounding JSON.
    /// </summary>
    [Fact]
    public void Redact_JsonQuotedSecretWithEscapedQuote_RedactsEntireValue()
    {
        var sut = CreateRedactor();
        var input = """{"password":"ab\"cd"}""";

        var result = sut.Redact(input);

        result.Should().Be("""{"password":"[REDACTED]"}""");
        result.Should().NotContain("ab");
        result.Should().NotContain("cd");
    }

    /// <summary>
    /// An unquoted "client_secret=" pair — not covered by any pattern before this fix, since the
    /// connection-string pattern's alternation omits client_secret and the generic key=value pattern's
    /// alternation only covered api_key/access_token/secret_key.
    /// </summary>
    [Fact]
    public void Redact_UnquotedClientSecret_RedactsValue()
    {
        var sut = CreateRedactor();
        var input = "client_secret=superSecret123";

        var result = sut.Redact(input);

        result.Should().Contain("[REDACTED]");
        result.Should().NotContain("superSecret123");
    }

    /// <summary>
    /// An unquoted "password:" pair (colon form) — the pre-existing connection-string pattern only
    /// matched "Password=" (equals form), leaving the colon form of the same key unredacted.
    /// </summary>
    [Fact]
    public void Redact_UnquotedPasswordWithColon_RedactsValue()
    {
        var sut = CreateRedactor();
        var input = "password: superSecret123";

        var result = sut.Redact(input);

        result.Should().Contain("[REDACTED]");
        result.Should().NotContain("superSecret123");
    }

    /// <summary>
    /// A secret keyword found inside a JSON string value (a query string embedded in a "url" field, on
    /// whitespace-free serialized JSON) is redacted without corrupting the surrounding document. A
    /// plain <c>\S+</c> value matcher is greedy across the entire rest of a whitespace-free string,
    /// consuming every remaining character — the closing quote, every later key, and the closing brace
    /// — and replacing it all with a single "[REDACTED]", destroying the rest of the payload's data.
    /// </summary>
    [Fact]
    public void Redact_SecretKeywordInsideJsonStringValue_DoesNotConsumeRestOfDocument()
    {
        var sut = CreateRedactor();
        var input = """{"url":"https://api.example.com/v1?api_key=abc123","method":"GET"}""";

        var result = sut.Redact(input);

        result.Should().Contain("[REDACTED]");
        result.Should().NotContain("abc123");
        // The rest of the JSON document must survive the redaction pass intact and remain valid JSON.
        var act = () => System.Text.Json.JsonDocument.Parse(result);
        act.Should().NotThrow();
        using var doc = System.Text.Json.JsonDocument.Parse(result);
        doc.RootElement.GetProperty("method").GetString().Should().Be("GET");
    }

    /// <summary>
    /// A bare (unquoted) key with a double-quoted value — e.g. a log line or shell env-var display —
    /// is redacted. Bounding the generic pattern's value class to [^;"'\s]+ alone (excluding quotes,
    /// to fix the JSON-corruption case above) makes the pattern quote-hostile: a value starting with a
    /// quote has nothing left to match at that position, so the whole match fails and the secret passes
    /// through completely unredacted. The value alternation must try a quoted form first.
    /// </summary>
    [Theory]
    [InlineData("api_key=\"sk-live-ABCDEF\"", "sk-live-ABCDEF")]
    [InlineData("api_key='sk-live-ABCDEF'", "sk-live-ABCDEF")]
    [InlineData("access_token=\"ghp_SECRETVALUE\"", "ghp_SECRETVALUE")]
    public void Redact_UnquotedKeyWithQuotedValue_RedactsValue(string input, string secret)
    {
        var sut = CreateRedactor();

        var result = sut.Redact(input);

        result.Should().Contain("[REDACTED]");
        result.Should().NotContain(secret);
    }

    /// <summary>
    /// A YAML-style "key: value" pair with a quoted value is redacted, and the original ":" separator
    /// survives in the output — the pre-existing replacement hardcoded "=" regardless of which
    /// separator the input actually used, which would rewrite "api_key: value" into the different,
    /// potentially document-invalidating "api_key=value".
    /// </summary>
    [Fact]
    public void Redact_ColonSeparatedKeyWithQuotedValue_PreservesColonSeparator()
    {
        var sut = CreateRedactor();
        var input = "api_key: \"sk-live-ABCDEF\"";

        var result = sut.Redact(input);

        result.Should().Be("api_key: [REDACTED]");
    }

    /// <summary>
    /// Storage/connection-string-shaped keys (AccountKey, SharedAccessKey, connection string, SAS
    /// token) are covered by the JSON-quoted pattern too, not just the semicolon-delimited
    /// connection-string pattern — which cannot match the JSON shape "AccountKey":"..." since the
    /// character after the key is a quote, not "=".
    /// </summary>
    [Theory]
    [InlineData("AccountKey")]
    [InlineData("SharedAccessKey")]
    [InlineData("connection_string")]
    [InlineData("sas_token")]
    public void Redact_JsonQuotedStorageKey_RedactsValue(string key)
    {
        var sut = CreateRedactor();
        var input = $$"""{"{{key}}":"b64-super-secret-value"}""";

        var result = sut.Redact(input);

        result.Should().Contain("[REDACTED]");
        result.Should().NotContain("b64-super-secret-value");
    }
}
