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
    public void BuildToolName_PreservesRawToolNameUnsanitized()
    {
        // The tool's own name is left exactly as the (untrusted) server declared it — matching how any
        // other MCP tool name is trusted today. Only the newly-injected server prefix is sanitized.
        var name = BundleOwnedMcpToolNaming.BuildToolName("b1:echo", "weird name!");

        name.Should().Be("b1_echo__weird name!");
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

    [Theory]
    [InlineData("bundle-abc123:echo", true)]
    [InlineData("other-bundle:epr-mcp", true)]
    [InlineData("granted-server", false)]
    [InlineData("epr-mcp", false)]
    public void IsNamespacedServerName_DetectsBundleOwnedKeyByColon(string serverName, bool expected)
    {
        BundleOwnedMcpToolNaming.IsNamespacedServerName(serverName).Should().Be(expected);
    }
}
