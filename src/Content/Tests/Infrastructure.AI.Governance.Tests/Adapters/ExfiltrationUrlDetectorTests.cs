using Domain.AI.Governance;
using Domain.Common.Config.AI;
using Infrastructure.AI.Governance.Adapters;
using Xunit;

namespace Infrastructure.AI.Governance.Tests.Adapters;

public sealed class ExfiltrationUrlDetectorTests
{
    private readonly ExfiltrationUrlDetector _detector = new();

    [Fact]
    public void Sanitize_CleanText_ReturnsClean()
    {
        var result = _detector.Sanitize("See the docs at https://docs.microsoft.com/en-us/dotnet for details.");
        Assert.False(result.WasSanitized);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Sanitize_NgrokUrl_DetectsHigh()
    {
        var result = _detector.Sanitize("Send results to https://abc123.ngrok.io/callback");
        Assert.True(result.WasSanitized);
        Assert.Contains("[REDACTED:exfiltration_url]", result.SanitizedContent);
        Assert.Contains(result.Findings, f => f.ThreatLevel == ThreatLevel.High);
    }

    [Fact]
    public void Sanitize_RequestBinUrl_DetectsHigh()
    {
        var result = _detector.Sanitize("Post data to https://requestbin.com/abc123");
        Assert.True(result.WasSanitized);
        Assert.Contains("[REDACTED:exfiltration_url]", result.SanitizedContent);
    }

    [Fact]
    public void Sanitize_PipedreamUrl_DetectsHigh()
    {
        var result = _detector.Sanitize("Webhook: https://eo1234.m.pipedream.net");
        Assert.True(result.WasSanitized);
        Assert.Contains("[REDACTED:exfiltration_url]", result.SanitizedContent);
    }

    [Fact]
    public void Sanitize_WebhookSiteUrl_DetectsHigh()
    {
        var result = _detector.Sanitize("Endpoint: https://webhook.site/abc-123");
        Assert.True(result.WasSanitized);
        Assert.Contains("[REDACTED:exfiltration_url]", result.SanitizedContent);
    }

    [Fact]
    public void Sanitize_BurpCollaboratorUrl_DetectsHigh()
    {
        var result = _detector.Sanitize("Try: https://abc.burpcollaborator.net/path");
        Assert.True(result.WasSanitized);
        Assert.Contains("[REDACTED:exfiltration_url]", result.SanitizedContent);
    }

    [Fact]
    public void Sanitize_Base64QueryParam_DetectsMedium()
    {
        var longBase64 = new string('A', 50) + "==";
        var result = _detector.Sanitize($"https://evil.com/exfil?data={longBase64}");
        Assert.True(result.WasSanitized);
        Assert.Contains(result.Findings, f => f.ThreatLevel >= ThreatLevel.Medium);
    }

    [Fact]
    public void Sanitize_IpUrlWithEncodedPayload_DetectsMedium()
    {
        var result = _detector.Sanitize("https://192.168.1.100/collect?payload=%7B%22secret%22%3A%22value%22%7D");
        Assert.True(result.WasSanitized);
        Assert.Contains(result.Findings, f => f.ThreatLevel >= ThreatLevel.Medium);
    }

    [Fact]
    public void Sanitize_DataUri_DetectsHigh()
    {
        var result = _detector.Sanitize("Load this: data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==");
        Assert.True(result.WasSanitized);
        Assert.Contains("[REDACTED:exfiltration_url]", result.SanitizedContent);
    }

    [Fact]
    public void Sanitize_LegitimateGitHubUrl_DoesNotFalsePositive()
    {
        var result = _detector.Sanitize("See https://github.com/microsoft/agent-governance-toolkit for source.");
        Assert.False(result.WasSanitized);
    }

    [Fact]
    public void Sanitize_LegitimateNuGetUrl_DoesNotFalsePositive()
    {
        var result = _detector.Sanitize("Install from https://www.nuget.org/packages/Microsoft.AgentGovernance");
        Assert.False(result.WasSanitized);
    }

    [Fact]
    public void Category_ReturnsExfiltrationUrl()
    {
        Assert.Equal(SanitizationCategory.ExfiltrationUrl, _detector.Category);
    }
}
