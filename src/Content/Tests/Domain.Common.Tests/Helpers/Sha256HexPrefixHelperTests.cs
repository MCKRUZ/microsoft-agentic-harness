using System.Security.Cryptography;
using System.Text;
using Domain.Common.Helpers;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Helpers;

/// <summary>
/// Tests for <see cref="Sha256HexPrefixHelper"/> — the shared disambiguating-suffix primitive
/// extracted from <c>BundleOwnedMcpToolNaming</c> and <c>ScopedCollectionName</c> under #377.
/// </summary>
public sealed class Sha256HexPrefixHelperTests
{
    [Fact]
    public void Compute_MatchesTheRawSha256ComputationItReplaces()
    {
        // Both callers this helper was extracted from independently computed
        // Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..N]. This pins the
        // helper to that exact computation, so extracting it cannot silently change either caller's
        // output for an already-persisted value (e.g. a tenant's derived RAG collection name).
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("contoso")))[..16];

        var actual = Sha256HexPrefixHelper.Compute("contoso", 16);

        actual.Should().Be(expected);
    }

    [Fact]
    public void Compute_IsDeterministic()
    {
        Sha256HexPrefixHelper.Compute("same-value", 10)
            .Should().Be(Sha256HexPrefixHelper.Compute("same-value", 10));
    }

    [Fact]
    public void Compute_DifferentInputs_ProduceDifferentOutput()
    {
        var a = Sha256HexPrefixHelper.Compute("my_server", 16);
        var b = Sha256HexPrefixHelper.Compute("my server", 16);

        a.Should().NotBe(b);
    }

    [Fact]
    public void Compute_ResultIsLowercaseHexOfRequestedLength()
    {
        var result = Sha256HexPrefixHelper.Compute("anything", 12);

        result.Should().HaveLength(12);
        result.Should().MatchRegex("^[0-9a-f]{12}$");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65)]
    public void Compute_HexLengthOutsideValidRange_Throws(int hexLength)
    {
        var act = () => Sha256HexPrefixHelper.Compute("value", hexLength);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Compute_FullSixtyFourCharLength_ReturnsTheEntireDigest()
    {
        var result = Sha256HexPrefixHelper.Compute("value", 64);

        result.Should().HaveLength(64);
    }
}
