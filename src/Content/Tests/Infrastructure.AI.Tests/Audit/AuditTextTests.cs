using Domain.AI.Telemetry.Redaction;
using FluentAssertions;
using Infrastructure.AI.Audit;
using Infrastructure.AI.Telemetry.Redaction;
using Xunit;

namespace Infrastructure.AI.Tests.Audit;

/// <summary>
/// <see cref="AuditText"/> redacts free text bound for a hash-chained trail (#903) while keeping the references a producer
/// put into it, and only those.
/// </summary>
public sealed class AuditTextTests
{
    [Fact]
    public void Redact_IssuedReference_SurvivesEveryRedactionCategory()
    {
        // A reference's hex is exactly what the digit rules match; the shield must hold for each category on its own, not
        // only for the default set taken together.
        var pseudonyms = AuditPseudonyms.Create();
        var reference = pseudonyms.Person("bob");
        var filter = new DefaultContentRedactionFilter();

        foreach (var category in RedactionCategories.All)
        {
            var redacted = AuditText.Redact($"By:{reference}|Reason:fine", new SingleCategory(filter, category), pseudonyms);

            redacted.Should().Be($"By:{reference}|Reason:fine", $"the {category} rule must not touch a shielded reference");
        }
    }

    [Fact]
    public void Redact_ReferenceShapedTextThisProcessDidNotIssue_IsRedactedLikeAnyOtherText()
    {
        // The key id is visible in the trail, so a forged token under it could otherwise carry 32 bytes of chosen data
        // (a card number spelled in hex) past the digit rules.
        var pseudonyms = AuditPseudonyms.Create();
        var forged = $"ref:{pseudonyms.KeyId}:4111111111111111{new string('0', 48)}";

        var redacted = AuditText.Redact($"reasoning {forged}", new DefaultContentRedactionFilter(), pseudonyms);

        redacted.Should().NotContain("4111111111111111");
    }

    [Fact]
    public void Issued_KnowsItsOwnTokensOnly()
    {
        var pseudonyms = AuditPseudonyms.Create();
        var reference = pseudonyms.Person("bob");
        var fingerprint = pseudonyms.Fingerprint("tool-argument", "x");

        pseudonyms.Issued(reference).Should().BeTrue();
        pseudonyms.Issued(fingerprint).Should().BeTrue();
        pseudonyms.Issued($"ref:{pseudonyms.KeyId}:{new string('a', 64)}").Should().BeFalse();
        AuditPseudonyms.Create().Issued(reference).Should().BeFalse("another instance did not make it");
    }

    private sealed class SingleCategory(DefaultContentRedactionFilter inner, RedactionCategory category)
        : Application.AI.Common.Interfaces.Telemetry.IContentRedactionFilter
    {
        public string Redact(string? content, IReadOnlyList<RedactionCategory> categories) => inner.Redact(content, [category]);
    }
}
