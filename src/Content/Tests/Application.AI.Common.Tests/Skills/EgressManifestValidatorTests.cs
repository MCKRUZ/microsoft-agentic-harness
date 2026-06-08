using Application.AI.Common.Skills;
using Domain.AI.Egress;
using Domain.AI.Skills;
using FluentAssertions;
using FluentValidation.TestHelper;
using Xunit;

namespace Application.AI.Common.Tests.Skills;

/// <summary>
/// PR-3c: validator-level tests for the per-skill egress manifest. Covers the
/// brief's four rejection rules: multi-label wildcards, full-regex host
/// patterns, non-http/https schemes, and out-of-range ports.
/// </summary>
public sealed class EgressManifestValidatorTests
{
    private readonly EgressManifestValidator _sut = new();

    private static EgressManifest WithEntry(EgressAllowlistEntry entry) =>
        new() { Allowlist = [entry] };

    /// <summary>
    /// Test 1: a well-formed manifest with one host and one leftmost-label pattern
    /// passes the full validator.
    /// </summary>
    [Fact]
    public void Validate_WellFormedManifest_HasNoErrors()
    {
        var manifest = new EgressManifest
        {
            Allowlist =
            [
                new EgressAllowlistEntry
                {
                    Host = "api.github.com",
                    Schemes = ["https"],
                    Ports = [443]
                },
                new EgressAllowlistEntry
                {
                    HostPattern = "*.azure-api.net",
                    Schemes = ["https"],
                    Ports = [443]
                }
            ]
        };

        _sut.TestValidate(manifest).ShouldNotHaveAnyValidationErrors();
    }

    /// <summary>
    /// Test 2 (brief): multi-label wildcards (anything beyond a single leading
    /// '*.') are rejected as SSRF vectors.
    /// </summary>
    [Theory]
    [InlineData("api.*.com")]      // wildcard in the middle
    [InlineData("**.foo.com")]     // double-star
    [InlineData("*.*.foo.com")]    // dotted multi-wildcard
    public void Validate_MultiLabelWildcard_FailsHostPatternRule(string pattern)
    {
        var manifest = WithEntry(new EgressAllowlistEntry
        {
            HostPattern = pattern,
            Schemes = ["https"],
            Ports = [443]
        });

        _sut.TestValidate(manifest)
            .ShouldHaveValidationErrorFor("Allowlist[0].HostPattern");
    }

    /// <summary>
    /// Test 3 (brief): a full-regex host pattern (e.g. ".*\.foo") is rejected
    /// because regex metacharacters in the host portion of an allowlist are an
    /// SSRF vector.
    /// </summary>
    [Theory]
    [InlineData(".*foo.com")]      // begins with regex
    [InlineData("*.foo[1-9].com")] // character class
    [InlineData("*.foo?.com")]     // single-char regex
    public void Validate_RegexInHostPattern_FailsHostPatternRule(string pattern)
    {
        var manifest = WithEntry(new EgressAllowlistEntry
        {
            HostPattern = pattern,
            Schemes = ["https"],
            Ports = [443]
        });

        _sut.TestValidate(manifest)
            .ShouldHaveValidationErrorFor("Allowlist[0].HostPattern");
    }

    /// <summary>
    /// Test 4 (brief): the scheme list rejects anything outside http and https.
    /// FTP / file / gopher are all SSRF vectors regardless of host.
    /// </summary>
    [Theory]
    [InlineData("ftp")]
    [InlineData("file")]
    [InlineData("gopher")]
    [InlineData("data")]
    public void Validate_NonHttpScheme_FailsSchemeRule(string scheme)
    {
        var manifest = WithEntry(new EgressAllowlistEntry
        {
            Host = "example.com",
            Schemes = [scheme],
            Ports = [443]
        });

        _sut.TestValidate(manifest)
            .ShouldHaveValidationErrorFor("Allowlist[0].Schemes[0]");
    }

    /// <summary>
    /// Ports outside [1, 65535] are rejected.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(int.MaxValue)]
    public void Validate_PortOutOfRange_FailsPortRule(int port)
    {
        var manifest = WithEntry(new EgressAllowlistEntry
        {
            Host = "example.com",
            Schemes = ["https"],
            Ports = [port]
        });

        _sut.TestValidate(manifest)
            .ShouldHaveValidationErrorFor("Allowlist[0].Ports[0]");
    }

    /// <summary>
    /// Both <c>Host</c> and <c>HostPattern</c> set is ambiguous and rejected.
    /// </summary>
    [Fact]
    public void Validate_HostAndPatternBothSet_Fails()
    {
        var manifest = WithEntry(new EgressAllowlistEntry
        {
            Host = "example.com",
            HostPattern = "*.example.com",
            Schemes = ["https"],
            Ports = [443]
        });

        _sut.TestValidate(manifest)
            .ShouldHaveValidationErrorFor("Allowlist[0]");
    }

    /// <summary>
    /// Neither <c>Host</c> nor <c>HostPattern</c> set is also ambiguous.
    /// </summary>
    [Fact]
    public void Validate_NeitherHostNorPatternSet_Fails()
    {
        var manifest = WithEntry(new EgressAllowlistEntry
        {
            Schemes = ["https"],
            Ports = [443]
        });

        _sut.TestValidate(manifest)
            .ShouldHaveValidationErrorFor("Allowlist[0]");
    }

    /// <summary>
    /// Empty schemes or ports lists are rejected — they match nothing and are
    /// useless. A consumer who omitted them probably meant to declare them.
    /// </summary>
    [Fact]
    public void Validate_EmptySchemes_Fails()
    {
        var manifest = WithEntry(new EgressAllowlistEntry
        {
            Host = "example.com",
            Schemes = [],
            Ports = [443]
        });

        _sut.TestValidate(manifest)
            .ShouldHaveValidationErrorFor("Allowlist[0].Schemes");
    }

    [Fact]
    public void Validate_EmptyPorts_Fails()
    {
        var manifest = WithEntry(new EgressAllowlistEntry
        {
            Host = "example.com",
            Schemes = ["https"],
            Ports = []
        });

        _sut.TestValidate(manifest)
            .ShouldHaveValidationErrorFor("Allowlist[0].Ports");
    }
}
