using Application.AI.Common.Services.Tools;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Services.Tools;

/// <summary>
/// Tests for <see cref="BundleOwnedMcpToolNaming"/> — the collision-proof naming scheme that closes a
/// privilege-escalation path a code review found in the original #368 design: granting a bundle's
/// self-reported MCP tool name verbatim let a malicious bundle advertise a tool named after a real,
/// more privileged host tool and get it auto-granted by name coincidence.
/// </summary>
public sealed class BundleOwnedMcpToolNamingTests
{
    [Fact]
    public void BuildToolName_SanitizesColonInServerKey()
    {
        var name = BundleOwnedMcpToolNaming.BuildToolName("bundle-abc123:echo", "search");

        name.Should().Be("bundle-abc123_echo__search");
    }

    [Fact]
    public void BuildToolName_SanitizesRawToolNameToo()
    {
        // A bundle-authored MCP server can name its tool anything, including characters OpenAI/Azure
        // OpenAI's function-name charset (^[a-zA-Z0-9_-]{1,64}$) rejects outright. Both the server prefix
        // and the tool's own name go through the same sanitization so the published name is always
        // callable, not just short enough. A disambiguating hash suffix is appended because sanitizing
        // actually changed the raw name (see BuildToolName_SanitizingDifferentRawNames_DoesNotCollide for
        // why that's load-bearing), so only the deterministic prefix is asserted here.
        var name = BundleOwnedMcpToolNaming.BuildToolName("b1:echo", "weird name!");

        name.Should().StartWith("b1_echo__weird_name_");
        name.Should().MatchRegex("^[a-zA-Z0-9_-]{1,64}$");
    }

    [Fact]
    public void BuildToolName_SanitizingDifferentRawNames_DoesNotCollide()
    {
        // Regression test for a correctness-review finding: two DIFFERENT raw tool names that sanitize to
        // the identical string (here, "get user" and "get.user" both collapse to "get_user") must not
        // produce the same namespaced name — the tool-chain's dedup-by-name step would silently drop one
        // of the two tools with no signal that anything was lost.
        var a = BundleOwnedMcpToolNaming.BuildToolName("b1:echo", "get user");
        var b = BundleOwnedMcpToolNaming.BuildToolName("b1:echo", "get.user");

        a.Should().NotBe(b);
    }

    [Fact]
    public void BuildToolName_SanitizingIdenticalRawNames_ProducesTheSameResult()
    {
        // Two calls with the letter-for-letter identical (already-messy) raw name must still agree — the
        // disambiguating hash is content-derived, not random, so the publisher and granter still land on
        // the same name for what is genuinely the same tool.
        var a = BundleOwnedMcpToolNaming.BuildToolName("b1:echo", "weird name!");
        var b = BundleOwnedMcpToolNaming.BuildToolName("b1:echo", "weird name!");

        a.Should().Be(b);
    }

    [Fact]
    public void BuildToolName_CannotProduceTheBareRealHostToolName()
    {
        // The core security property: no matter what a bundle's own server calls its tool, the
        // namespaced result can never equal the bare name a real host tool is registered under, because
        // the separator and prefix are never absent.
        var name = BundleOwnedMcpToolNaming.BuildToolName("bundle-evil:server", "delete_all_data");

        name.Should().NotBe("delete_all_data");
        name.Should().EndWith("__delete_all_data");
    }

    [Fact]
    public void BuildToolName_DifferentServers_ProduceDifferentNamesForTheSameToolName()
    {
        var a = BundleOwnedMcpToolNaming.BuildToolName("bundle-a:echo", "search");
        var b = BundleOwnedMcpToolNaming.BuildToolName("bundle-b:echo", "search");

        a.Should().NotBe(b);
    }

    [Fact]
    public void BuildToolName_NaturalConcatenationUnder64Chars_IsReturnedVerbatim()
    {
        // A realistic bundle id (GUID) + short server + short tool name fits comfortably — the common
        // case must not pay the shortening cost or lose readability.
        var name = BundleOwnedMcpToolNaming.BuildToolName(
            "3fa85f64-5717-4562-b3fc-2c963f66afa6:search", "lookup");

        name.Should().Be("3fa85f64-5717-4562-b3fc-2c963f66afa6_search__lookup");
        name.Length.Should().BeLessThanOrEqualTo(64);
    }

    [Theory]
    [InlineData(
        "3fa85f64-5717-4562-b3fc-2c963f66afa6:a-fairly-descriptive-mcp-server-name",
        "a-fairly-descriptive-tool-name-too")]
    [InlineData("b:server", "this-tool-name-alone-is-already-past-sixty-four-characters-long-total")]
    public void BuildToolName_OverLongConcatenation_IsCappedAt64Chars(string server, string tool)
    {
        // Both an ordinary (non-adversarial) long bundle+server+tool combination and a single
        // pathologically long raw tool name must still respect the provider-enforced limit — OpenAI and
        // Azure OpenAI both reject any function name over 64 characters outright.
        var name = BundleOwnedMcpToolNaming.BuildToolName(server, tool);

        name.Length.Should().BeLessThanOrEqualTo(64);
    }

    [Fact]
    public void BuildToolName_OverLongConcatenation_IsDeterministic()
    {
        // BundleRunExecutor (granter) and ToolChainBuilder (publisher) each call this independently and
        // must land on the exact same namespaced name for the grant to match the published tool.
        var server = "3fa85f64-5717-4562-b3fc-2c963f66afa6:a-fairly-descriptive-mcp-server-name";
        var tool = "a-fairly-descriptive-tool-name-too";

        var a = BundleOwnedMcpToolNaming.BuildToolName(server, tool);
        var b = BundleOwnedMcpToolNaming.BuildToolName(server, tool);

        a.Should().Be(b);
    }

    [Fact]
    public void BuildToolName_OverLongConcatenation_DifferentToolsOnSameServerDoNotCollide()
    {
        var server = "3fa85f64-5717-4562-b3fc-2c963f66afa6:a-fairly-descriptive-mcp-server-name";

        var a = BundleOwnedMcpToolNaming.BuildToolName(server, "a-fairly-descriptive-tool-name-one");
        var b = BundleOwnedMcpToolNaming.BuildToolName(server, "a-fairly-descriptive-tool-name-two");

        a.Should().NotBe(b);
    }

    [Fact]
    public void BuildToolName_OverLongConcatenation_StillCannotProduceTheBareRealHostToolName()
    {
        var name = BundleOwnedMcpToolNaming.BuildToolName(
            "bundle-evil:a-very-long-attacker-chosen-server-name-here",
            "a-very-long-attacker-chosen-tool-name-meant-to-collide-with-delete_all_data");

        name.Should().NotBe("delete_all_data");
    }
}
