using Domain.AI.Escalation;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Escalation;

public sealed class EscalationCallDigestTests
{
    private static Dictionary<string, string> Args(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    [Fact]
    public void Compute_SameCallInAnyOrder_IsTheSameDigest()
    {
        var a = EscalationCallDigest.Compute("tool", Args(("path", "/x"), ("mode", "r")));
        var b = EscalationCallDigest.Compute("tool", Args(("mode", "r"), ("path", "/x")));

        b.Should().Be(a, "two renderings of the same call must agree");
        a.Should().MatchRegex("^sha256:[0-9a-f]{64}$");
    }

    [Fact]
    public void Compute_ToolNameValueOrKeyChanges_ChangesTheDigest()
    {
        var baseline = EscalationCallDigest.Compute("tool", Args(("path", "/x")));

        EscalationCallDigest.Compute("other", Args(("path", "/x"))).Should().NotBe(baseline);
        EscalationCallDigest.Compute("tool", Args(("path", "/y"))).Should().NotBe(baseline);
        EscalationCallDigest.Compute("tool", Args(("file", "/x"))).Should().NotBe(baseline);
    }

    [Fact]
    public void Compute_FieldsThatConcatenateIdentically_DoNotCollide()
    {
        // Without length prefixes, key "ab" + value "c" and key "a" + value "bc" would hash the same text.
        EscalationCallDigest.Compute("t", Args(("ab", "c")))
            .Should().NotBe(EscalationCallDigest.Compute("t", Args(("a", "bc"))));
    }

    [Fact]
    public void DisplayKey_ReservedNameIsEscaped_OrdinaryNameIsUntouched()
    {
        EscalationCallDigest.DisplayKey(EscalationCallDigest.DigestKey).Should().Be("argument (call digest)");
        EscalationCallDigest.DisplayKey(EscalationCallDigest.TruncatedKey).Should().Be("argument (truncated)");
        EscalationCallDigest.DisplayKey("path").Should().Be("path");
    }
}
