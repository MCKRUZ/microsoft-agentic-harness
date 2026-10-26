using System.Security.Cryptography;
using System.Text;
using Application.AI.Common.Interfaces.Telemetry;
using Application.AI.Common.Services.Telemetry;
using Domain.Common.Config;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.Tests.Services.Telemetry;

/// <summary>
/// The telemetry pseudonymizer (#903): every telemetry and log pseudonym is a keyed hash, so a reader of the traces or
/// logs cannot confirm what a person sent or who they are by hashing a guess.
/// </summary>
public sealed class HmacTelemetryPseudonymizerTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    private static HmacTelemetryPseudonymizer Create(string? key, ILogger<HmacTelemetryPseudonymizer>? logger = null)
    {
        var config = new AppConfig();
        config.Observability.PiiFiltering.HmacKey = key;
        return new HmacTelemetryPseudonymizer(Options.Create(config), logger ?? NullLogger<HmacTelemetryPseudonymizer>.Instance);
    }

    [Fact]
    public void HashAttribute_ConfiguredKey_IsHmacSha256OfTheValue()
    {
        // The exact output the PII span filter produced before it moved here, so existing dashboards keep matching.
        var hash = Create(Convert.ToBase64String(Key)).HashAttribute("user@example.com");

        hash.Should().Be(Convert.ToHexStringLower(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes("user@example.com"))));
    }

    [Fact]
    public void Fingerprint_ConfiguredKey_IsKeyedAndNotThePlainSha256AGuessCouldConfirm()
    {
        const string arguments = "{\"path\":\"/home/alice/medical.txt\"}";

        var fingerprint = Create(Convert.ToBase64String(Key)).Fingerprint(ITelemetryPseudonymizer.Purposes.ToolArguments, arguments);

        fingerprint.Should().Be(Convert.ToHexStringLower(
            HMACSHA256.HashData(PurposeKey(ITelemetryPseudonymizer.Purposes.ToolArguments), Encoding.UTF8.GetBytes(arguments))));
        fingerprint.Should().NotBe(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(arguments))),
            "the plain hash is what the trace carried before, and anyone can recompute it from a guess");
    }

    [Fact]
    public void Fingerprint_SameText_DiffersFromTheAttributeHashAndBetweenPurposes()
    {
        // A tool argument that happens to equal an email must not join to that person's attribute hash.
        var pseudonymizer = Create(Convert.ToBase64String(Key));

        var attribute = pseudonymizer.HashAttribute("user@example.com");
        var tool = pseudonymizer.Fingerprint(ITelemetryPseudonymizer.Purposes.ToolArguments, "user@example.com");
        var other = pseudonymizer.Fingerprint("other-purpose", "user@example.com");

        new[] { attribute, tool, other }.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Fingerprint_CannotBeReachedThroughAnAttributeHashOfCraftedText()
    {
        // Whoever controls a hashed attribute must not be able to compute a purpose's fingerprint of a guess, which a
        // purpose prefix on one shared key would allow ("purpose\0value" hashed as an attribute).
        var pseudonymizer = Create(Convert.ToBase64String(Key));
        const string purpose = ITelemetryPseudonymizer.Purposes.McpAuditArguments;

        pseudonymizer.HashAttribute(purpose + "\0{\"a\":1}").Should().NotBe(pseudonymizer.Fingerprint(purpose, "{\"a\":1}"));
        pseudonymizer.Fingerprint("ab", "c").Should().NotBe(pseudonymizer.Fingerprint("a", "bc"));
    }

    [Fact]
    public void Create_ConfiguredKey_GivesTheSamePseudonymsInEveryInstance()
    {
        var key = Convert.ToBase64String(Key);

        Create(key).HashAttribute("enduser-1").Should().Be(Create(key).HashAttribute("enduser-1"),
            "per-person counts must add up across restarts and instances when the key is configured");
    }

    [Fact]
    public void Create_NoKey_UsesAPrivateRandomKeyAndWarns()
    {
        var logger = new CapturingLogger();

        var first = Create(null, logger);
        var second = Create(string.Empty);

        first.HashAttribute("enduser-1").Should().MatchRegex("^[0-9a-f]{64}$");
        first.HashAttribute("enduser-1").Should().NotBe(second.HashAttribute("enduser-1"),
            "each process makes its own key, which nobody holds");
        first.HashAttribute("enduser-1").Should().NotBe(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("enduser-1"))));
        logger.Warnings.Should().ContainSingle().Which.Should().Contain("Observability:PiiFiltering:HmacKey");
    }

    [Theory]
    [InlineData("not base64 at all!")]
    [InlineData("AAECAwQFBgcICQoLDA0ODw==")] // valid base64, 16 bytes
    public void Create_KeySetButUnusable_Throws(string key)
    {
        // A key someone meant to set but got wrong would otherwise hash on quietly under a key nobody intended.
        var act = () => Create(key);

        act.Should().Throw<InvalidOperationException>().WithMessage("*HmacKey*32 bytes*");
    }

    private static byte[] PurposeKey(string purpose) =>
        HKDF.Expand(HashAlgorithmName.SHA256, Key, 32, Encoding.UTF8.GetBytes(purpose));

    private sealed class CapturingLogger : ILogger<HmacTelemetryPseudonymizer>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }
}
