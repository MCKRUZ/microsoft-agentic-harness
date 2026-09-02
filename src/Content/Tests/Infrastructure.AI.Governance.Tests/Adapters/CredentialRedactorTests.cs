using System.Reflection;
using System.Text.RegularExpressions;
using Domain.AI.Governance;
using Domain.Common.Config.AI;
using Infrastructure.AI.Governance.Adapters;
using Xunit;

namespace Infrastructure.AI.Governance.Tests.Adapters;

public sealed class CredentialRedactorTests
{
    private readonly CredentialRedactor _redactor = new();

    [Fact]
    public void GeneratedPatterns_AllHaveAFiniteMatchTimeout()
    {
        RegexTimeoutAssertions.AssertAllHaveFiniteMatchTimeout(typeof(CredentialRedactor));
    }

    [Fact]
    public void Sanitize_CleanText_ReturnsClean()
    {
        var result = _redactor.Sanitize("The database returned 42 rows successfully.");
        Assert.False(result.WasSanitized);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Sanitize_AwsAccessKey_RedactsAndReportsHigh()
    {
        var result = _redactor.Sanitize("Key is AKIAIOSFODNN7EXAMPLE for the account.");
        Assert.True(result.WasSanitized);
        Assert.Contains("[REDACTED:aws_key]", result.SanitizedContent);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", result.SanitizedContent);
        Assert.Single(result.Findings);
        Assert.Equal(SanitizationCategory.CredentialLeak, result.Findings[0].Category);
        Assert.Equal(ThreatLevel.High, result.Findings[0].ThreatLevel);
    }

    [Fact]
    public void Sanitize_AzureConnectionString_RedactsAndReportsHigh()
    {
        var connStr = "DefaultEndpointsProtocol=https;AccountName=myacct;AccountKey=abc123def456==;EndpointSuffix=core.windows.net";
        var result = _redactor.Sanitize($"Connection: {connStr}");
        Assert.True(result.WasSanitized);
        Assert.Contains("[REDACTED:azure_connection_string]", result.SanitizedContent);
        Assert.DoesNotContain("AccountKey=", result.SanitizedContent);
    }

    [Fact]
    public void Sanitize_JwtToken_RedactsAndReportsHigh()
    {
        var jwt = $"eyJ{new string('a', 20)}.eyJ{new string('b', 20)}.{new string('c', 20)}";
        var result = _redactor.Sanitize($"Token: {jwt}");
        Assert.True(result.WasSanitized);
        Assert.Contains("[REDACTED:jwt]", result.SanitizedContent);
        Assert.DoesNotContain("eyJa", result.SanitizedContent);
    }

    [Fact]
    public void Sanitize_GitHubPat_RedactsAndReportsHigh()
    {
        var result = _redactor.Sanitize("Use ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefgh to authenticate.");
        Assert.True(result.WasSanitized);
        Assert.Contains("[REDACTED:github_pat]", result.SanitizedContent);
    }

    [Fact]
    public void Sanitize_OpenAiApiKey_RedactsAndReportsHigh()
    {
        var result = _redactor.Sanitize("Set OPENAI_API_KEY=sk-proj-abcdefghijklmnopqrstuv");
        Assert.True(result.WasSanitized);
        Assert.Contains("[REDACTED:api_key]", result.SanitizedContent);
    }

    [Fact]
    public void Sanitize_SlackToken_RedactsAndReportsHigh()
    {
        var result = _redactor.Sanitize("Bot token: xoxb-1234567890123-abcdefghij");
        Assert.True(result.WasSanitized);
        Assert.Contains("[REDACTED:slack_token]", result.SanitizedContent);
    }

    [Fact]
    public void Sanitize_PrivateKeyBlock_RedactsAndReportsHigh()
    {
        var pem = "-----BEGIN RSA PRIVATE KEY-----\nMIIEpAIBAAK...\n-----END RSA PRIVATE KEY-----";
        var result = _redactor.Sanitize($"Cert:\n{pem}");
        Assert.True(result.WasSanitized);
        Assert.Contains("[REDACTED:private_key]", result.SanitizedContent);
    }

    // ── PrivateKeyPattern: unbounded span via NonBacktracking, not a length cap (#580 round 2) ──

    [Fact]
    public void Sanitize_RealisticallySizedPrivateKeyBody_StillRedacts()
    {
        var body = string.Join("\n", Enumerable.Repeat(new string('M', 64), 60)); // ~4160 chars
        var pem = $"-----BEGIN RSA PRIVATE KEY-----\n{body}\n-----END RSA PRIVATE KEY-----";

        var result = _redactor.Sanitize($"Key:\n{pem}");

        Assert.True(result.WasSanitized);
        Assert.Contains("[REDACTED:private_key]", result.SanitizedContent);
    }

    [Fact]
    public void Sanitize_VeryLargePrivateKeyBody_StillRedacts()
    {
        // Round 1 of this fix bounded the gap between BEGIN/END to 8000 characters — security review
        // measured that this silently stops matching real keys larger than the bound (an 11,309-
        // character 16384-bit RSA body was NOT matched, and RedactionCategory has no private-key
        // member to catch what this pattern misses). NonBacktracking removes the need for any length
        // cap: this proves a body well past the old 8000-char bound still redacts in full.
        var body = string.Join("\n", Enumerable.Repeat(new string('M', 64), 180)); // ~11,700 chars
        var pem = $"-----BEGIN RSA PRIVATE KEY-----\n{body}\n-----END RSA PRIVATE KEY-----";

        var result = _redactor.Sanitize($"Key:\n{pem}");

        Assert.True(result.WasSanitized);
        Assert.Contains("[REDACTED:private_key]", result.SanitizedContent);
        Assert.DoesNotContain("MMMM", result.SanitizedContent);
    }

    [Fact]
    public void Sanitize_UnclosedBeginMarkerOverMillionsOfCharacters_CompletesWithoutTimingOut()
    {
        // The actual ReDoS this pattern is exposed to, and what RegexOptions.NonBacktracking — not a
        // length bound — fixes: a BEGIN marker with no matching END forces the engine to search to
        // end-of-string for a marker that never arrives. Security review measured this at ~2000ms
        // (timing out) under the default backtracking engine even WITH round 1's 8000-char bound in
        // place, since the bound only shrinks the search window, not the backtracking cost within it.
        // Mutation test: remove RegexOptions.NonBacktracking and this throws RegexMatchTimeoutException
        // or takes multiple seconds instead of completing quickly.
        var content = "-----BEGIN RSA PRIVATE KEY-----\n" + new string('M', 3_000_000);

        var act = () => _redactor.Sanitize(content);

        Assert.Null(Record.Exception(act));
    }

    [Theory]
    [InlineData("OPENSSH ")]
    [InlineData("ENCRYPTED ")]
    public void Sanitize_AdditionalKeyTypes_RedactsAndReportsHigh(string keyType)
    {
        // Widened from the original RSA/EC/DSA-only prefix list, found while re-reviewing this pattern
        // for the NonBacktracking fix above: OpenSSH has been ssh-keygen's default export format since
        // 2014, and an encrypted PKCS8 container is a common one too — neither was covered before.
        var pem = $"-----BEGIN {keyType}PRIVATE KEY-----\nMIIEpAIBAAK...\n-----END {keyType}PRIVATE KEY-----";

        var result = _redactor.Sanitize($"Key:\n{pem}");

        Assert.True(result.WasSanitized);
        Assert.Contains("[REDACTED:private_key]", result.SanitizedContent);
    }

    [Fact]
    public void Sanitize_GenericSecretKeyValue_RedactsWithLowerConfidence()
    {
        var result = _redactor.Sanitize("password=SuperSecret123!");
        Assert.True(result.WasSanitized);
        Assert.Contains("[REDACTED:generic_secret]", result.SanitizedContent);
        Assert.True(result.Findings[0].Confidence < 0.8);
    }

    [Fact]
    public void Sanitize_BasicAuthHeader_RedactsAndReportsHigh()
    {
        var result = _redactor.Sanitize("Authorization: Basic dXNlcjpwYXNzd29yZA==");
        Assert.True(result.WasSanitized);
        Assert.Contains("[REDACTED:basic_auth]", result.SanitizedContent);
    }

    [Fact]
    public void Sanitize_MultipleSecrets_RedactsAll()
    {
        var content = "Key: AKIAIOSFODNN7EXAMPLE, token: ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefgh";
        var result = _redactor.Sanitize(content);
        Assert.True(result.WasSanitized);
        Assert.True(result.Findings.Count >= 2);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", result.SanitizedContent);
        Assert.DoesNotContain("ghp_ABCDEFGHIJ", result.SanitizedContent);
    }

    [Fact]
    public void Sanitize_NormalTextWithKeyword_DoesNotFalsePositive()
    {
        var result = _redactor.Sanitize("The skeleton key pattern is useful in DI.");
        Assert.False(result.WasSanitized);
    }

    [Fact]
    public void Category_ReturnsCredentialLeak()
    {
        Assert.Equal(SanitizationCategory.CredentialLeak, _redactor.Category);
    }

    // ── Per-pattern fail-open (#580 security-review HIGH finding) ──

    [Fact]
    public void ScanAndRedact_PatternTimesOut_ReturnsContentUnchangedWithNoFinding()
    {
        // Security-review HIGH finding: a timeout used to unwind ScanAndRedact's CALLER (Sanitize),
        // skipping every remaining pattern in the same call, not just the one that timed out. Invoked
        // directly via reflection since the nine production patterns are no longer individually
        // vulnerable to this (the two that were are now RegexOptions.NonBacktracking) — this proves the
        // isolation mechanism itself, independent of which pattern eventually needs it.
        // Mutation test: remove ScanAndRedact's inner try/catch around Matches and this throws instead
        // of returning content unchanged.
        var method = typeof(CredentialRedactor).GetMethod(
            "ScanAndRedact", BindingFlags.NonPublic | BindingFlags.Static)!;
        var pathological = new Regex(@"(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(1));
        var content = new string('a', 40) + "!";
        var findings = new List<SanitizationFinding>();

        var result = (string)method.Invoke(
            null, [content, pathological, "test_type", ThreatLevel.High, 0.9, findings, "test-tool"])!;

        Assert.Equal(content, result);
        Assert.Empty(findings);
    }
}
