using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Application.AI.Common.Tests.Helpers;

/// <summary>
/// Unit tests for <see cref="ToolPayloadRedactor"/>. The load-bearing invariant here is fail-loud, not
/// fail-open: a redactor that violates <see cref="ISecretRedactor.Redact"/>'s contract (returning
/// <see langword="null"/> for non-null input) must never result in the raw, unredacted payload silently
/// reaching a caller — see <see cref="Redact_ContractViolatingRedactor_ThrowsRatherThanLeakingRawPayload"/>.
/// </summary>
public sealed class ToolPayloadRedactorTests
{
    private sealed class NullReturningRedactor : ISecretRedactor
    {
        public string? Redact(string? input) => null;
        public bool IsSecretKey(string configKey) => false;
    }

    private sealed class MarkerRedactor : ISecretRedactor
    {
        public const string Secret = "super-secret-value";
        public const string Replacement = "[REDACTED]";

        public string? Redact(string? input) => input?.Replace(Secret, Replacement);
        public bool IsSecretKey(string configKey) => false;
    }

    [Fact]
    public void Redact_NullRedactor_ReturnsPayloadUnchanged()
    {
        var result = ToolPayloadRedactor.Redact("api_key=super-secret", redactor: null);

        result.Should().Be("api_key=super-secret");
    }

    [Fact]
    public void Redact_ContractViolatingRedactor_ThrowsRatherThanLeakingRawPayload()
    {
        var redactor = new NullReturningRedactor();

        var act = () => ToolPayloadRedactor.Redact("api_key=super-secret", redactor);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*violating*")
            .Which.Message.Should().NotContain("super-secret");
    }

    [Fact]
    public void RedactAndTruncate_RedactsBeforeTruncating()
    {
        var redactor = new MarkerRedactor();
        var padding = new string('x', 20);
        var payload = $"{padding}{MarkerRedactor.Secret}";
        var redactedLength = padding.Length + MarkerRedactor.Replacement.Length;

        // maxLength sits just above the fully-redacted length, but well below the raw payload length —
        // long enough to keep the whole "[REDACTED]" replacement intact, too short to have kept an
        // unredacted secret if truncation had run before redaction instead of after.
        var maxLength = redactedLength + 2;
        payload.Length.Should().BeGreaterThan(maxLength, "the test must force real truncation to be meaningful");

        var result = ToolPayloadRedactor.RedactAndTruncate(payload, redactor, maxLength);

        result.Should().Contain(MarkerRedactor.Replacement);
        result.Should().NotContain("super-secret");
    }

    [Fact]
    public void RedactAndTruncate_NullRedactor_StillTruncates()
    {
        var payload = new string('a', 50);

        var result = ToolPayloadRedactor.RedactAndTruncate(payload, redactor: null, maxLength: 10);

        result.Should().Be(new string('a', 10));
    }

    [Fact]
    public void RedactForStreaming_UnderCeiling_RedactsAndIsNotWithheld()
    {
        var redactor = new MarkerRedactor();
        var payload = $"before {MarkerRedactor.Secret} after";

        var result = ToolPayloadRedactor.RedactForStreaming(payload, redactor, NullLogger.Instance, "unused");

        result.Withheld.Should().BeFalse();
        result.Json.Should().Contain(MarkerRedactor.Replacement).And.NotContain("super-secret");
    }

    [Fact]
    public void RedactForStreaming_AboveCeiling_WithholdsWithoutRedacting()
    {
        var payload = new string('x', ToolPayloadRedactor.MaxStreamedToolCallArgsLength + 1);

        var result = ToolPayloadRedactor.RedactForStreaming(payload, redactor: null, NullLogger.Instance, "unused");

        result.Withheld.Should().BeTrue();
        result.Json.Should().Be("{}");
    }

    /// <summary>
    /// A redaction-contract violation must withhold, not silently pass through as a normal
    /// (Withheld: false) empty-object result — the two failure modes ("too large" and "redaction
    /// broke") must be indistinguishable to the client precisely because both mean "the real
    /// arguments never arrived," and a caller must never mistake either for genuinely empty
    /// arguments.
    /// </summary>
    [Fact]
    public void RedactForStreaming_RedactorThrows_WithholdsRatherThanReturningNormalEmptyObject()
    {
        var redactor = new NullReturningRedactor();

        var result = ToolPayloadRedactor.RedactForStreaming("api_key=super-secret", redactor, NullLogger.Instance, "unused");

        result.Withheld.Should().BeTrue();
        result.Json.Should().Be("{}");
    }
}
