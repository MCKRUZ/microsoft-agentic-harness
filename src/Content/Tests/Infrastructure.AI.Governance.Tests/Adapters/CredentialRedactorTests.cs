using Domain.AI.Governance;
using Domain.Common.Config.AI;
using Infrastructure.AI.Governance.Adapters;
using Xunit;

namespace Infrastructure.AI.Governance.Tests.Adapters;

public sealed class CredentialRedactorTests
{
    private readonly CredentialRedactor _redactor = new();

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
}
