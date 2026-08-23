using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Telemetry;
using Domain.AI.Governance;
using Domain.AI.Telemetry.Redaction;
using Domain.Common.Config.Observability;
using FluentAssertions;
using Infrastructure.Observability.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.Observability.Tests.Logging;

/// <summary>Tests for <see cref="ContentFilterLocalLogRedactor"/> (#457).</summary>
public sealed class ContentFilterLocalLogRedactorTests
{
    private const string Marker = "SECRET";
    private const string Redacted = "[REDACTED]";

    private static Mock<IContentRedactionFilter> MockFilter()
    {
        var mock = new Mock<IContentRedactionFilter>();
        mock.Setup(f => f.Redact(It.IsAny<string?>(), It.IsAny<IReadOnlyList<RedactionCategory>>()))
            .Returns((string? s, IReadOnlyList<RedactionCategory> _) => s?.Replace(Marker, Redacted) ?? string.Empty);
        return mock;
    }

    /// <summary>A sanitizer that returns content unchanged — the answer a real one gives to clean text.</summary>
    private static Mock<ICompositeResponseSanitizer> PassthroughSanitizer()
    {
        var mock = new Mock<ICompositeResponseSanitizer>();
        mock.Setup(s => s.Sanitize(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((string content, string? _) => SanitizationResult.Clean(content));
        return mock;
    }

    private static IOptionsMonitor<LogsConfig> ConfigOf(LogsConfig config) =>
        Mock.Of<IOptionsMonitor<LogsConfig>>(m => m.CurrentValue == config);

    [Fact]
    public void Enabled_ReflectsRedactionEnabled_IndependentOfOtelExportEnabled()
    {
        // #457's whole point: local-sink redaction must not depend on the OTel bridge being on.
        var config = new LogsConfig { OtelExportEnabled = false, RedactionEnabled = true };
        var redactor = new ContentFilterLocalLogRedactor(
            PassthroughSanitizer().Object, MockFilter().Object, ConfigOf(config));

        redactor.Enabled.Should().BeTrue();
    }

    [Fact]
    public void Enabled_RedactionDisabled_ReturnsFalse()
    {
        var config = new LogsConfig { RedactionEnabled = false };
        var redactor = new ContentFilterLocalLogRedactor(
            PassthroughSanitizer().Object, MockFilter().Object, ConfigOf(config));

        redactor.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Redact_MatchingText_ScrubsUsingConfiguredCategories()
    {
        var filter = MockFilter();
        var config = new LogsConfig { RedactionEnabled = true, RedactionCategories = ["Email", "Generic"] };
        var redactor = new ContentFilterLocalLogRedactor(
            PassthroughSanitizer().Object, filter.Object, ConfigOf(config));

        var result = redactor.Redact($"token is {Marker}");

        result.Should().Be($"token is {Redacted}");
        filter.Verify(f => f.Redact(
            $"token is {Marker}",
            It.Is<IReadOnlyList<RedactionCategory>>(c =>
                c.Contains(RedactionCategory.Email) && c.Contains(RedactionCategory.Generic))),
            Times.Once);
    }

    [Fact]
    public void Redact_NoValidCategoriesConfigured_FallsBackToFullSetRatherThanSkippingRedaction()
    {
        // Same fail-safe-not-open posture as LogRecordRedactionProcessor.
        var filter = MockFilter();
        var config = new LogsConfig { RedactionEnabled = true, RedactionCategories = ["not-a-real-category"] };
        var redactor = new ContentFilterLocalLogRedactor(
            PassthroughSanitizer().Object, filter.Object, ConfigOf(config));

        var result = redactor.Redact($"token is {Marker}");

        result.Should().Be($"token is {Redacted}");
        filter.Verify(f => f.Redact(
            It.IsAny<string?>(),
            It.Is<IReadOnlyList<RedactionCategory>>(c => c.Count == Enum.GetValues<RedactionCategory>().Length)),
            Times.Once);
    }

    /// <summary>
    /// Review finding on #457: this redactor called the filter directly, skipping the sanitize step
    /// every other redaction path (#470) already has — so a secret split by invisible/zero-width
    /// characters (which the sanitizer canonicalizes away, but the redaction filter's anchored
    /// patterns do not) could dodge redaction specifically on local sinks (console, file, JSONL, named
    /// pipe) while the identical string was caught everywhere else. Proven by ordering: the redaction
    /// filter mock only sees the marker if it ran against the sanitizer's joined-back-together output,
    /// not the raw split text.
    /// </summary>
    [Fact]
    public void Redact_SanitizesBeforeRedacting()
    {
        const string split = "secret is AKIA<split>ABCDEFGHIJ123456";
        const string joined = "secret is AKIAABCDEFGHIJ123456";

        var sanitizer = new Mock<ICompositeResponseSanitizer>();
        sanitizer.Setup(s => s.Sanitize(split, It.IsAny<string?>())).Returns(SanitizationResult.Clean(joined));

        var filter = new Mock<IContentRedactionFilter>();
        filter.Setup(f => f.Redact(It.IsAny<string?>(), It.IsAny<IReadOnlyList<RedactionCategory>>()))
            .Returns((string? s, IReadOnlyList<RedactionCategory> _) => s == joined ? "[REDACTED:AwsKey]" : s ?? string.Empty);

        var config = new LogsConfig { RedactionEnabled = true, RedactionCategories = ["AwsKey"] };
        var redactor = new ContentFilterLocalLogRedactor(sanitizer.Object, filter.Object, ConfigOf(config));

        var result = redactor.Redact(split);

        result.Should().Be("[REDACTED:AwsKey]",
            "redaction must run against the sanitizer's output, which joined the split key back together");
    }

    [Fact]
    public void Constructor_NullSanitizer_Throws()
    {
        var config = new LogsConfig();
        var act = () => new ContentFilterLocalLogRedactor(null!, MockFilter().Object, ConfigOf(config));

        act.Should().Throw<ArgumentNullException>();
    }
}
