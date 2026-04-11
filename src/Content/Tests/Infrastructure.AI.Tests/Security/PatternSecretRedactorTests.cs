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
}
