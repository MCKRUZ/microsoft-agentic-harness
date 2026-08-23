using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Telemetry;
using Application.AI.Common.Services.Governance;
using Domain.AI.Governance;
using Domain.AI.Telemetry.Redaction;
using FluentAssertions;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Services.Governance;

/// <summary>Tests for <see cref="SanitizeThenRedact"/> (#470).</summary>
public sealed class SanitizeThenRedactTests
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

    [Fact]
    public void Apply_SanitizesThenRedacts_InOrder()
    {
        var sanitizer = new Mock<ICompositeResponseSanitizer>();
        sanitizer.Setup(s => s.Sanitize("raw", null)).Returns(SanitizationResult.Clean("sanitized"));
        var filter = new Mock<IContentRedactionFilter>();
        filter.Setup(f => f.Redact("sanitized", It.IsAny<IReadOnlyList<RedactionCategory>>()))
            .Returns("redacted");

        var result = SanitizeThenRedact.Apply("raw", sanitizer.Object, filter.Object, [RedactionCategory.Generic]);

        result.Should().Be("redacted");
    }

    /// <summary>
    /// Independent security review finding (M2): the three production call sites that route through
    /// this shared combinator (span/log content) had no bound on how much text reaches the
    /// sanitizer/redaction regex chain, unlike <c>Tools.ReportedFailureText.PrepareForReporting</c>,
    /// which already established a 64KB ceiling for exactly this reason — bounding worst-case
    /// regex-scan cost on a remotely-triggered, attacker-controlled string before any pattern runs.
    /// </summary>
    [Fact]
    public void Apply_TextOverMaxScanLength_IsCutBeforeSanitizingOrRedacting()
    {
        var oversized = new string('x', SanitizeThenRedact.MaxScanLength + 500);
        var sanitizer = new Mock<ICompositeResponseSanitizer>();
        string? sanitizerSawLength = null;
        sanitizer.Setup(s => s.Sanitize(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((string content, string? _) =>
            {
                sanitizerSawLength = content.Length.ToString();
                return SanitizationResult.Clean(content);
            });

        SanitizeThenRedact.Apply(oversized, sanitizer.Object, IdentityFilter().Object, [RedactionCategory.Generic]);

        sanitizerSawLength.Should().Be(SanitizeThenRedact.MaxScanLength.ToString(),
            "the sanitizer must never see more than the scan-length ceiling, regardless of input size");
    }

    [Fact]
    public void Apply_TextUnderMaxScanLength_IsUnaffectedByTheBound()
    {
        var sanitizer = PassthroughSanitizer();

        var result = SanitizeThenRedact.Apply("a short string", sanitizer.Object, IdentityFilter().Object, [RedactionCategory.Generic]);

        result.Should().Be("a short string");
    }

    [Fact]
    public void Apply_SanitizedEmpty_NoHook_RedactsTheEmptyResultAsNoOp()
    {
        var sanitizer = new Mock<ICompositeResponseSanitizer>();
        sanitizer.Setup(s => s.Sanitize(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(SanitizationResult.Clean(string.Empty));

        var result = SanitizeThenRedact.Apply("raw", sanitizer.Object, IdentityFilter().Object, [RedactionCategory.Generic]);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Apply_SanitizedEmpty_WithHook_ReturnsTheHooksResultInsteadOfRedacting()
    {
        var sanitizer = new Mock<ICompositeResponseSanitizer>();
        sanitizer.Setup(s => s.Sanitize(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(SanitizationResult.Clean(string.Empty));
        var filter = new Mock<IContentRedactionFilter>(MockBehavior.Strict);

        var result = SanitizeThenRedact.Apply(
            "raw", sanitizer.Object, filter.Object, [RedactionCategory.Generic],
            onSanitizedEmpty: _ => "[withheld]");

        result.Should().Be("[withheld]");
        filter.Verify(f => f.Redact(It.IsAny<string?>(), It.IsAny<IReadOnlyList<RedactionCategory>>()), Times.Never);
    }
}
