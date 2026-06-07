using Domain.AI.Egress;
using FluentAssertions;
using Infrastructure.AI.Egress;
using Infrastructure.AI.Tests.Egress.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.AI.Tests.Egress;

public sealed class DefaultEgressPolicyTests
{
    private static DefaultEgressPolicy NewPolicy(params EgressAllowlistEntry[] entries) =>
        new(entries, NullLogger<DefaultEgressPolicy>.Instance, TimeProvider.System);

    [Fact]
    public async Task ExactHost_Match_Allows()
    {
        var policy = NewPolicy(new EgressAllowlistEntry
        {
            Host = "api.github.com",
            Schemes = ["https"],
            Ports = [443]
        });

        var decision = await policy.AllowAsync(
            new Uri("https://api.github.com/users/octocat"),
            TestIdentity.Default,
            CancellationToken.None);

        decision.Allowed.Should().BeTrue();
        decision.MatchedAllowlistEntry.Should().Be("api.github.com");
    }

    [Fact]
    public async Task ExactHost_Mismatch_Denies()
    {
        var policy = NewPolicy(new EgressAllowlistEntry
        {
            Host = "api.github.com",
            Schemes = ["https"],
            Ports = [443]
        });

        var decision = await policy.AllowAsync(
            new Uri("https://evil.example.com/"),
            TestIdentity.Default,
            CancellationToken.None);

        decision.Allowed.Should().BeFalse();
        decision.MatchedAllowlistEntry.Should().BeNull();
    }

    [Fact]
    public async Task Wildcard_LeftmostLabel_MatchesOneLabel()
    {
        var policy = NewPolicy(new EgressAllowlistEntry
        {
            HostPattern = "*.azure-api.net",
            Schemes = ["https"],
            Ports = [443]
        });

        var decision = await policy.AllowAsync(
            new Uri("https://foo.azure-api.net/"),
            TestIdentity.Default,
            CancellationToken.None);

        decision.Allowed.Should().BeTrue();
        decision.MatchedAllowlistEntry.Should().Be("*.azure-api.net");
    }

    [Fact]
    public async Task Wildcard_DoesNotMatchBareSuffix()
    {
        var policy = NewPolicy(new EgressAllowlistEntry
        {
            HostPattern = "*.azure-api.net",
            Schemes = ["https"],
            Ports = [443]
        });

        var decision = await policy.AllowAsync(
            new Uri("https://azure-api.net/"),
            TestIdentity.Default,
            CancellationToken.None);

        decision.Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task Wildcard_DoesNotMatchMultipleLabels()
    {
        // TLS-cert semantics: *.example.com matches exactly one leading label.
        var policy = NewPolicy(new EgressAllowlistEntry
        {
            HostPattern = "*.azure-api.net",
            Schemes = ["https"],
            Ports = [443]
        });

        var decision = await policy.AllowAsync(
            new Uri("https://foo.bar.azure-api.net/"),
            TestIdentity.Default,
            CancellationToken.None);

        decision.Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task SchemeMismatch_Denies()
    {
        var policy = NewPolicy(new EgressAllowlistEntry
        {
            Host = "api.github.com",
            Schemes = ["https"],
            Ports = [80]
        });

        var decision = await policy.AllowAsync(
            new Uri("http://api.github.com/"),
            TestIdentity.Default,
            CancellationToken.None);

        decision.Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task PortMismatch_Denies()
    {
        var policy = NewPolicy(new EgressAllowlistEntry
        {
            Host = "api.github.com",
            Schemes = ["https"],
            Ports = [443]
        });

        var decision = await policy.AllowAsync(
            new Uri("https://api.github.com:8443/"),
            TestIdentity.Default,
            CancellationToken.None);

        decision.Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task NonHttpScheme_Denies()
    {
        // Non-HTTP schemes are categorically rejected before host matching.
        var policy = NewPolicy(new EgressAllowlistEntry
        {
            Host = "anything",
            Schemes = ["file", "gopher", "ftp"],
            Ports = [21, 80, 443]
        });

        var decision = await policy.AllowAsync(
            new Uri("ftp://anything/"),
            TestIdentity.Default,
            CancellationToken.None);

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Contain("Scheme");
    }

    [Fact]
    public async Task EmptyAllowlist_DefaultDeny()
    {
        var policy = NewPolicy(); // no entries

        var decision = await policy.AllowAsync(
            new Uri("https://api.github.com/"),
            TestIdentity.Default,
            CancellationToken.None);

        decision.Allowed.Should().BeFalse();
    }

    [Theory]
    [InlineData("api.*.com")]              // wildcard not in leftmost position
    [InlineData("*.")]                       // suffix empty
    [InlineData("*foo.com")]                 // missing dot after star
    [InlineData("*.foo[bar].com")]           // regex metacharacters
    [InlineData("*.com")]                    // suffix has no dot
    public void MalformedWildcard_ThrowsAtConstruction(string pattern)
    {
        var act = () => NewPolicy(new EgressAllowlistEntry
        {
            HostPattern = pattern,
            Schemes = ["https"],
            Ports = [443]
        });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EntryWithBothHostAndPattern_ThrowsAtConstruction()
    {
        var act = () => NewPolicy(new EgressAllowlistEntry
        {
            Host = "api.github.com",
            HostPattern = "*.github.com",
            Schemes = ["https"],
            Ports = [443]
        });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EntryWithNeitherHostNorPattern_ThrowsAtConstruction()
    {
        var act = () => NewPolicy(new EgressAllowlistEntry
        {
            Schemes = ["https"],
            Ports = [443]
        });

        act.Should().Throw<ArgumentException>();
    }
}
