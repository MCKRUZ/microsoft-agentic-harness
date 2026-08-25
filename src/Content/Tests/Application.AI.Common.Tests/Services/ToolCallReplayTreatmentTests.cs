using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Telemetry;
using Application.AI.Common.Services;
using Domain.AI.Governance;
using Domain.AI.Telemetry.Redaction;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Conversations;
using FluentAssertions;
using Infrastructure.AI.Governance.Adapters;
using Infrastructure.AI.Security;
using Infrastructure.AI.Telemetry.Redaction;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Services;

public sealed class ToolCallReplayTreatmentTests
{
    private static Mock<ICompositeResponseSanitizer> PassthroughSanitizer()
    {
        var mock = new Mock<ICompositeResponseSanitizer>();
        mock.Setup(s => s.Sanitize(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((string content, string? _) => SanitizationResult.Clean(content));
        return mock;
    }

    private static Mock<IContentRedactionFilter> IdentityFilter()
    {
        var mock = new Mock<IContentRedactionFilter>();
        mock.Setup(f => f.Redact(It.IsAny<string?>(), It.IsAny<IReadOnlyList<RedactionCategory>>()))
            .Returns((string? s, IReadOnlyList<RedactionCategory> _) => s ?? string.Empty);
        return mock;
    }

    private static Mock<Application.AI.Common.Interfaces.ISecretRedactor> IdentitySecretRedactor()
    {
        var mock = new Mock<Application.AI.Common.Interfaces.ISecretRedactor>();
        mock.Setup(r => r.Redact(It.IsAny<string>())).Returns((string s) => s);
        return mock;
    }

    private static ToolCallReplayTreatment CreateTreatment(
        ICompositeResponseSanitizer sanitizer,
        IContentRedactionFilter redactionFilter,
        int maxVerbatimChars = 8192,
        bool enabled = true,
        Application.AI.Common.Interfaces.ISecretRedactor? secretRedactor = null)
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                Conversations = new ConversationsConfig
                {
                    ToolCallReplay = new ToolCallReplayConfig
                    {
                        MaxVerbatimChars = maxVerbatimChars,
                        Enabled = enabled
                    }
                }
            }
        };
        var monitor = Mock.Of<IOptionsMonitor<AppConfig>>(m => m.CurrentValue == appConfig);

        return new ToolCallReplayTreatment(
            sanitizer, redactionFilter, secretRedactor ?? IdentitySecretRedactor().Object, monitor,
            NullLogger<ToolCallReplayTreatment>.Instance);
    }

    [Fact]
    public void Enabled_ReflectsConfiguredValue()
    {
        CreateTreatment(PassthroughSanitizer().Object, IdentityFilter().Object, enabled: true)
            .Enabled.Should().BeTrue();
        CreateTreatment(PassthroughSanitizer().Object, IdentityFilter().Object, enabled: false)
            .Enabled.Should().BeFalse();
    }

    [Fact]
    public void Treat_ShortText_SanitizesThenRedacts_InOrder()
    {
        var sanitizer = new Mock<ICompositeResponseSanitizer>();
        sanitizer.Setup(s => s.Sanitize("raw", "search")).Returns(SanitizationResult.Clean("sanitized"));
        var filter = new Mock<IContentRedactionFilter>();
        filter.Setup(f => f.Redact("sanitized", It.IsAny<IReadOnlyList<RedactionCategory>>()))
            .Returns("redacted");

        var treatment = CreateTreatment(sanitizer.Object, filter.Object);

        treatment.Treat("raw", "search").Should().Be("redacted");
    }

    [Fact]
    public void Treat_EmptyText_PassesThroughUnchanged()
    {
        var treatment = CreateTreatment(PassthroughSanitizer().Object, IdentityFilter().Object);

        treatment.Treat(string.Empty, "search").Should().Be(string.Empty);
    }

    [Fact]
    public void Treat_TextUnderVerbatimCeiling_IsNotTruncated()
    {
        var treatment = CreateTreatment(
            PassthroughSanitizer().Object, IdentityFilter().Object, maxVerbatimChars: 100);

        var text = new string('a', 50);

        treatment.Treat(text, "search").Should().Be(text);
    }

    [Fact]
    public void Treat_TextOverVerbatimCeilingButUnderWithholdCeiling_IsTruncatedWithMarker()
    {
        var treatment = CreateTreatment(
            PassthroughSanitizer().Object, IdentityFilter().Object, maxVerbatimChars: 100);

        var text = new string('a', 200);

        var result = treatment.Treat(text, "search");

        result.Should().HaveLength(100);
        result.Should().EndWith("…[truncated]");
    }

    [Fact]
    public void Treat_TextOverWithholdCeiling_ReturnsOversizedPlaceholder_WithoutSanitizingOrRedacting()
    {
        var sanitizer = new Mock<ICompositeResponseSanitizer>(MockBehavior.Strict);
        var filter = new Mock<IContentRedactionFilter>(MockBehavior.Strict);
        var treatment = CreateTreatment(sanitizer.Object, filter.Object);

        var text = new string('a', ToolCallReplayTreatment.WithholdCeilingChars + 1);

        var result = treatment.Treat(text, "search");

        result.Should().Contain("withheld").And.Contain("Re-invoke this tool");
        // MockBehavior.Strict means either mock throwing on ANY call would fail this test —
        // the oversized check must short-circuit before sanitize/redact ever run.
    }

    [Fact]
    public void Treat_SanitizerEmptiesContent_ReturnsEmptyAfterSanitizationPlaceholder()
    {
        var sanitizer = new Mock<ICompositeResponseSanitizer>();
        sanitizer.Setup(s => s.Sanitize(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(SanitizationResult.Clean(string.Empty));
        var treatment = CreateTreatment(sanitizer.Object, IdentityFilter().Object);

        var result = treatment.Treat("some hostile input", "search");

        result.Should().Contain("withheld").And.Contain("sanitization");
    }

    [Fact]
    public void Treat_SanitizerThrows_ReturnsProcessingFailedPlaceholder_NeverThrows()
    {
        var sanitizer = new Mock<ICompositeResponseSanitizer>();
        sanitizer.Setup(s => s.Sanitize(It.IsAny<string>(), It.IsAny<string?>()))
            .Throws(new InvalidOperationException("boom"));
        var treatment = CreateTreatment(sanitizer.Object, IdentityFilter().Object);

        var act = () => treatment.Treat("raw", "search");

        act.Should().NotThrow();
        treatment.Treat("raw", "search").Should().Contain("withheld").And.Contain("could not be safely processed");
    }

    [Fact]
    public void Treat_ConfiguredMaxVerbatimCharsAboveWithholdCeiling_ClampsToWithholdCeiling()
    {
        // Defense in depth: even if validation is somehow bypassed, the treatment service itself
        // must not honor a configured ceiling above the point where redaction stops being trustworthy.
        var treatment = CreateTreatment(
            PassthroughSanitizer().Object, IdentityFilter().Object,
            maxVerbatimChars: ToolCallReplayTreatment.WithholdCeilingChars + 10_000);

        var text = new string('a', ToolCallReplayTreatment.WithholdCeilingChars); // exactly at the real ceiling

        // Should NOT be withheld (it's exactly at, not over, the ceiling) and should NOT be truncated
        // (clamped MaxVerbatimChars == WithholdCeilingChars, so this fits verbatim).
        treatment.Treat(text, "search").Should().Be(text);
    }

    [Fact]
    public void NoResultPlaceholder_IsStableAndNonEmpty()
    {
        var treatment = CreateTreatment(PassthroughSanitizer().Object, IdentityFilter().Object);

        treatment.NoResultPlaceholder.Should().NotBeNullOrWhiteSpace();
        treatment.NoResultPlaceholder.Should().Be(treatment.NoResultPlaceholder);
    }

    /// <summary>
    /// The load-bearing verification the plan called for: sanitizing JSON containing zero-width
    /// characters inside a string literal must not corrupt the JSON structure. Uses the REAL
    /// sanitizer chain (not mocks) — this is a property of the actual injection scrubber, not of
    /// this class's own logic, so a mock would prove nothing.
    /// </summary>
    [Fact]
    public void Treat_JsonWithZeroWidthCharactersInStringLiteral_RemainsValidJsonAfterTreatment()
    {
        var realSanitizer = new CompositeResponseSanitizer(
        [
            new CredentialRedactor(),
            new ResponseInjectionScrubber(),
            new ExfiltrationUrlDetector(),
        ]);
        var realFilter = new DefaultContentRedactionFilter();
        var treatment = CreateTreatment(realSanitizer, realFilter);

        // A zero-width space (U+200B) split across "sec​ret" inside a JSON string value — the
        // shape a secret-splitting evasion attempt would take.
        const string zeroWidthSpace = "​";
        var json = $$"""{"note":"sec{{zeroWidthSpace}}ret value"}""";

        var result = treatment.Treat(json, "search");

        var act = () => System.Text.Json.JsonDocument.Parse(result);
        act.Should().NotThrow("sanitizing a JSON payload must never produce structurally invalid JSON");
    }

    /// <summary>
    /// Security-review finding H-1: <see cref="ICompositeResponseSanitizer"/> and
    /// <see cref="IContentRedactionFilter"/> are value-shape regex scanners with no JSON-key-name
    /// awareness — neither matches a quote-terminated key like <c>"token":</c>. Uses the REAL
    /// <see cref="PatternSecretRedactor"/> (the same structural, key-name-aware redactor that already
    /// protects the transient SSE stream and trace store for identical content) to prove the durable,
    /// model-facing replay path is now at least as strong as those, not weaker.
    /// </summary>
    [Fact]
    public void Treat_JsonWithQuotedSecretKey_RedactsTheValue()
    {
        var realSanitizer = new CompositeResponseSanitizer(
        [
            new CredentialRedactor(),
            new ResponseInjectionScrubber(),
            new ExfiltrationUrlDetector(),
        ]);
        var realFilter = new DefaultContentRedactionFilter();
        var realSecretRedactor = new PatternSecretRedactor([]);
        var treatment = CreateTreatment(realSanitizer, realFilter, secretRedactor: realSecretRedactor);

        var json = """{"token":"abcdef1234567890zzzz"}""";

        var result = treatment.Treat(json, "http_request");

        result.Should().NotContain("abcdef1234567890zzzz",
            "a JSON-quoted secret key must be redacted before this content is persisted and replayed " +
            "to the model on every later turn");
    }

    /// <summary>
    /// Security-gate finding: the 64KB ceiling was checked only against the RAW input, but sanitize
    /// and redact can GROW text. A sub-ceiling payload that expands past the ceiling would then be
    /// handed to <see cref="PatternSecretRedactor"/>, which silently skips its structural JSON walk
    /// above that same size and degrades to a regex-only scan — losing exactly the protection the
    /// structural pass was added to provide, on durable model-facing content. The ceiling is now
    /// re-checked after treatment, and an expanded payload is withheld rather than under-redacted.
    /// </summary>
    [Fact]
    public void Treat_TreatmentExpandsPayloadPastCeiling_WithholdsRatherThanUnderRedacting()
    {
        // A sanitizer that expands its input, standing in for real redaction replacing a short secret
        // with a longer placeholder. Input sits just under the ceiling; output crosses it.
        var expandingSanitizer = new Mock<ICompositeResponseSanitizer>();
        expandingSanitizer
            .Setup(s => s.Sanitize(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((string content, string? _) => SanitizationResult.Clean(content + new string('x', 4096)));

        var treatment = CreateTreatment(expandingSanitizer.Object, IdentityFilter().Object);

        var justUnderCeiling = new string('a', ToolCallReplayTreatment.WithholdCeilingChars - 1024);

        var result = treatment.Treat(justUnderCeiling, "search");

        result.Should().NotContain("aaaa",
            "a payload that crosses the structural-redaction ceiling DURING treatment must be withheld, " +
            "not passed to a redactor that silently stops doing structural redaction above that size");
        result.Length.Should().BeLessThan(ToolCallReplayTreatment.WithholdCeilingChars);
    }

    [Fact]
    public void Treat_TreatmentKeepsPayloadUnderCeiling_StillReturnsTreatedContent()
    {
        // The control for the test above: the same expanding sanitizer on a small input stays under
        // the ceiling, so treatment proceeds normally. Without this, the assertion above would pass
        // just as well against a method that withheld everything.
        var expandingSanitizer = new Mock<ICompositeResponseSanitizer>();
        expandingSanitizer
            .Setup(s => s.Sanitize(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((string content, string? _) => SanitizationResult.Clean(content + new string('x', 4096)));

        var treatment = CreateTreatment(expandingSanitizer.Object, IdentityFilter().Object);

        var result = treatment.Treat("small payload", "search");

        result.Should().Contain("small payload");
    }
}
