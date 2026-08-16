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

    /// <summary>
    /// A secret key nested inside a JSON string value whose quotes are escaped — e.g. a tool argument
    /// carrying an HTTP request body serialized as a string field — is redacted. Neither the
    /// JSON-quoted pattern (which needs an unescaped <c>"key"</c>) nor the generic key=value pattern
    /// (whose separator can't span the escaped <c>\":</c>) can match this shape; only a structural
    /// walk that re-parses the string value as its own JSON document catches it.
    /// </summary>
    [Fact]
    public void Redact_SecretKeyInsideEscapedNestedJsonString_RedactsValue()
    {
        var sut = CreateRedactor();
        var input = """{"body":"{\"api_key\":\"sk-1\"}"}""";

        var result = sut.Redact(input);

        result.Should().NotContain("sk-1");
        using var doc = System.Text.Json.JsonDocument.Parse(result!);
        var nested = System.Text.Json.JsonDocument.Parse(doc.RootElement.GetProperty("body").GetString()!);
        nested.RootElement.GetProperty("api_key").GetString().Should().Be("[REDACTED]");
    }

    /// <summary>
    /// A non-JSON leaf string (a URL) inside an otherwise valid, non-nested JSON document still gets
    /// its embedded secret keyword redacted via the free-text fallback the structural walk applies to
    /// every leaf — proving the leaf-level regex scan still fires, not just the top-level one.
    /// </summary>
    [Fact]
    public void Redact_SecretKeywordInsideUnparseableStringInsideValidJson_StillRedacted()
    {
        var sut = CreateRedactor();
        var input = """{"headers":"Authorization: Bearer eyABC123xyz==","method":"GET"}""";

        var result = sut.Redact(input);

        result.Should().NotContain("eyABC123xyz==");
        using var doc = System.Text.Json.JsonDocument.Parse(result!);
        doc.RootElement.GetProperty("headers").GetString().Should().Be("Authorization: Bearer [REDACTED]");
        doc.RootElement.GetProperty("method").GetString().Should().Be("GET");
    }

    /// <summary>
    /// A secret key inside an object nested in a JSON array is redacted — proves array elements are
    /// recursed into, not just object properties.
    /// </summary>
    [Fact]
    public void Redact_ArrayOfObjectsWithSecretKeys_RedactsEach()
    {
        var sut = CreateRedactor();
        var input = """{"items":[{"name":"a","api_key":"k1"},{"name":"b","api_key":"k2"}]}""";

        var result = sut.Redact(input);

        result.Should().NotContain("k1");
        result.Should().NotContain("k2");
        using var doc = System.Text.Json.JsonDocument.Parse(result!);
        var items = doc.RootElement.GetProperty("items");
        items[0].GetProperty("api_key").GetString().Should().Be("[REDACTED]");
        items[1].GetProperty("api_key").GetString().Should().Be("[REDACTED]");
        items[0].GetProperty("name").GetString().Should().Be("a");
    }

    /// <summary>
    /// Valid JSON with no secrets anywhere is returned as the exact same string reference — the
    /// structural walk must not silently reformat a clean payload on every call. Mutation-tested: a
    /// version of RedactNode that always reports "changed" (even when nothing was replaced) fails
    /// this test while still passing every value-bearing test above.
    /// </summary>
    [Fact]
    public void Redact_ValidJsonWithNoSecrets_ReturnsOriginalReferenceUnchanged()
    {
        var sut = CreateRedactor();
        var input = """{"toolName":"http_call","timeout":30,"tags":["a","b"]}""";

        var result = sut.Redact(input);

        ReferenceEquals(result, input).Should().BeTrue();
    }

    /// <summary>
    /// Already-redacted JSON (the structural shape, not just the regex-pattern shape covered by
    /// <see cref="Redact_AlreadyRedactedJsonQuotedSecret_ReturnsUnchanged"/>) reports no change and
    /// returns the original reference — the secret-key branch must compare against the existing
    /// value, not unconditionally overwrite and only happen to reserialize identically.
    /// </summary>
    [Fact]
    public void Redact_JsonAlreadyStructurallyRedacted_ReturnsOriginalReferenceUnchanged()
    {
        var sut = CreateRedactor();
        var input = """{"api_key":"[REDACTED]","timeout":30}""";

        var result = sut.Redact(input);

        ReferenceEquals(result, input).Should().BeTrue();
    }

    /// <summary>
    /// A non-object, non-array JSON value under a secret key (a number, not a string) is still
    /// replaced with the string placeholder — the structural pass redacts by key, regardless of the
    /// value's original JSON type.
    /// </summary>
    [Fact]
    public void Redact_NonStringValueUnderSecretKey_IsRedacted()
    {
        var sut = CreateRedactor();
        var input = """{"secret_key":123456}""";

        var result = sut.Redact(input);

        result.Should().NotContain("123456");
        using var doc = System.Text.Json.JsonDocument.Parse(result!);
        doc.RootElement.GetProperty("secret_key").GetString().Should().Be("[REDACTED]");
    }

    /// <summary>
    /// Malformed JSON that merely starts with <c>{</c> (so it passes the cheap prefix pre-check) but
    /// fails to actually parse falls back to the regex-only pass rather than throwing.
    /// </summary>
    [Fact]
    public void Redact_MalformedJsonStartingWithBrace_FallsBackToRegexOnly()
    {
        var sut = CreateRedactor();
        var input = "{not valid json, api_key=superSecret123";

        var result = sut.Redact(input);

        result.Should().Contain("[REDACTED]");
        result.Should().NotContain("superSecret123");
    }

    /// <summary>
    /// A JSON payload larger than the structural pass's size ceiling still gets its unescaped secret
    /// redacted, via the regex-only fallback rather than the structural walk.
    /// </summary>
    [Fact]
    public void Redact_JsonAboveSizeCeiling_FallsBackToRegexOnly()
    {
        var sut = CreateRedactor();
        var padding = new string('x', 70 * 1024);
        var input = $$"""{"padding":"{{padding}}","api_key":"sk-oversized"}""";

        var result = sut.Redact(input);

        result.Should().Contain("[REDACTED]");
        result.Should().NotContain("sk-oversized");
    }

    /// <summary>
    /// Proves the size ceiling is actually active (not just harmlessly redundant): an oversized
    /// payload whose secret is only reachable via the structural walk (escaped-nested-JSON, the
    /// #391 shape) is NOT redacted once input exceeds the ceiling, because only the structural pass
    /// — not the regex fallback — can see through the escaping. Documents the residual gap rather
    /// than hiding it. If the ceiling check were ever removed, this secret would start being
    /// redacted and this test would fail, distinguishing it from
    /// <see cref="Redact_JsonAboveSizeCeiling_FallsBackToRegexOnly"/>, which passes whether or not
    /// the ceiling exists.
    /// </summary>
    [Fact]
    public void Redact_EscapedNestedSecretAboveSizeCeiling_IsNotRedacted_DocumentingTheResidualGap()
    {
        var sut = CreateRedactor();
        var padding = new string('x', 70 * 1024);
        var input = $$"""{"padding":"{{padding}}","body":"{\"api_key\":\"sk-deep-oversized\"}"}""";

        var result = sut.Redact(input);

        result.Should().Contain("sk-deep-oversized",
            "above the size ceiling only the regex fallback runs, which cannot see through the " +
            "escaped nesting — this is the documented residual gap, not a regression");
    }

    /// <summary>
    /// A secret nested two levels deep in escaped JSON-in-a-string-in-a-string — within the embedded
    /// JSON depth cap — is redacted at the innermost level. Payloads are built via
    /// <see cref="System.Text.Json.JsonSerializer"/> rather than hand-written escaping so the exact
    /// backslash nesting is generated correctly instead of guessed at.
    /// </summary>
    [Fact]
    public void Redact_SecretTwoLevelsDeepInEscapedJson_IsRedacted()
    {
        var sut = CreateRedactor();
        var innermost = System.Text.Json.JsonSerializer.Serialize(new { api_key = "sk-deep" });
        var middle = System.Text.Json.JsonSerializer.Serialize(new { inner = innermost });
        var input = System.Text.Json.JsonSerializer.Serialize(new { body = middle });

        var result = sut.Redact(input);

        result.Should().NotContain("sk-deep");
        using var doc = System.Text.Json.JsonDocument.Parse(result!);
        var middleDoc = System.Text.Json.JsonDocument.Parse(doc.RootElement.GetProperty("body").GetString()!);
        var innerDoc = System.Text.Json.JsonDocument.Parse(middleDoc.RootElement.GetProperty("inner").GetString()!);
        innerDoc.RootElement.GetProperty("api_key").GetString().Should().Be("[REDACTED]");
    }

    /// <summary>
    /// Common real-world secret key names — security-review finding H1 — measured as leaking
    /// unredacted before <see cref="PatternSecretRedactor"/>'s key alternation covered them. Each of
    /// these is a shape a real HTTP tool call or header dictionary would plausibly carry.
    /// </summary>
    [Theory]
    [InlineData("x-api-key")]
    [InlineData("refresh_token")]
    [InlineData("id_token")]
    [InlineData("private_key")]
    [InlineData("secret_access_key")]
    [InlineData("authorization")]
    [InlineData("auth_token")]
    [InlineData("credential")]
    [InlineData("credentials")]
    [InlineData("passphrase")]
    [InlineData("subscription_key")]
    [InlineData("Ocp-Apim-Subscription-Key")]
    public void Redact_CommonSecretKeyNames_AreRedacted(string key)
    {
        var sut = CreateRedactor();
        var input = $$"""{"{{key}}":"LEAKME"}""";

        var result = sut.Redact(input);

        result.Should().NotContain("LEAKME");
    }

    /// <summary>
    /// Duplicate JSON property names are legal per RFC 8259 and <see cref="System.Text.Json.Nodes.JsonNode"/>.Parse
    /// tolerates them, but materializing a <see cref="System.Text.Json.Nodes.JsonObject"/>'s backing
    /// dictionary rejects the duplicate with an <see cref="ArgumentException"/> — security-review
    /// finding M2, a regression this structural pass would otherwise introduce into
    /// <c>ToolOutputCompressionBehavior</c>, which redacts fully third-party-controlled tool output.
    /// Must degrade to the regex-only fallback, not throw out of the caller.
    /// </summary>
    [Fact]
    public void Redact_JsonWithDuplicatePropertyNames_FallsBackToRegexOnly_DoesNotThrow()
    {
        var sut = CreateRedactor();
        var input = """{"a":1,"a":2,"api_key":"LEAKME"}""";

        var act = () => sut.Redact(input);

        act.Should().NotThrow();
        act().Should().NotContain("LEAKME");
    }

    /// <summary>
    /// Bare "token" and bare "secret" — not the compound shapes
    /// <see cref="Redact_CommonSecretKeyNames_AreRedacted"/> already covers — are common real key
    /// names on their own (a session store keyed just "token", a config value keyed just "secret").
    /// </summary>
    [Theory]
    [InlineData("token")]
    [InlineData("secret")]
    public void Redact_BareTokenOrSecretKeyName_IsRedacted(string key)
    {
        var sut = CreateRedactor();
        var input = $$"""{"{{key}}":"LEAKME"}""";

        var result = sut.Redact(input);

        result.Should().NotContain("LEAKME");
    }

    /// <summary>
    /// A secret key with incidental leading/trailing whitespace (a sloppily-serialized tool argument,
    /// not necessarily adversarial) is still recognized — the anchored key match trims before
    /// comparing, or "api_key " (trailing space) would silently fail the whole-key match.
    /// </summary>
    [Fact]
    public void Redact_SecretKeyWithSurroundingWhitespace_IsStillRedacted()
    {
        var sut = CreateRedactor();
        var input = """{"api_key ":"LEAKME"}""";

        var result = sut.Redact(input);

        result.Should().NotContain("LEAKME");
    }

    /// <summary>
    /// A top-level JSON-encoded STRING — exactly what <c>JsonSerializer.Serialize(someString)</c>
    /// produces for a string value, e.g. a tool result that is itself a JSON document serialized as
    /// text — previously bypassed the structural pass entirely: LooksLikeJson only accepted the
    /// object/array openers `{`/`[`, so this shape fell straight to the regex-only fallback, which
    /// cannot see through an escaped-nested secret. The structural walk already handles a top-level
    /// JsonValue string via RedactStringLeaf; only the entry-point prefix check was missing the case
    /// — security-review finding M3.
    /// </summary>
    [Fact]
    public void Redact_TopLevelJsonEncodedString_RedactsNestedSecret()
    {
        var sut = CreateRedactor();
        var input = System.Text.Json.JsonSerializer.Serialize(
            System.Text.Json.JsonSerializer.Serialize(new { api_key = "sk-topstring" }));

        var result = sut.Redact(input);

        result.Should().NotContain("sk-topstring");
        var nestedJson = System.Text.Json.JsonSerializer.Deserialize<string>(result!)!;
        using var doc = System.Text.Json.JsonDocument.Parse(nestedJson);
        doc.RootElement.GetProperty("api_key").GetString().Should().Be("[REDACTED]");
    }
}
