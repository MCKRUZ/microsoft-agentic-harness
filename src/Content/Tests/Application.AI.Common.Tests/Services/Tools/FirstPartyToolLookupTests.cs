using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Tools;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Application.AI.Common.Tests.Services.Tools;

/// <summary>
/// Tests for <see cref="FirstPartyToolLookup"/> — the bounded-key-set-gated lookup shared by
/// <see cref="ToolCapabilityResolver"/> and <c>ToolPermissionProfileResolver</c>, extracted so the
/// two resolvers cannot drift on this safety invariant (found duplicated during code review).
/// </summary>
public sealed class FirstPartyToolLookupTests
{
    [Fact]
    public void Resolve_NameInBoundedSetAndRegistered_ReturnsTheTool()
    {
        var tool = Mock.Of<ITool>();
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool>("file_system", (_, _) => tool);
        var lookup = new FirstPartyToolLookup(
            services.BuildServiceProvider(), new HashSet<string> { "file_system" });

        lookup.Resolve("file_system").Should().BeSameAs(tool);
    }

    [Fact]
    public void Resolve_NameOutsideBoundedSet_ReturnsNullWithoutProbingContainer()
    {
        // Registered in the container but NOT in the bounded key set — e.g. an MCP or bundle-owned
        // name. Must resolve to null without ever calling GetKeyedService for it.
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool>("mcp_tool", (_, _) => Mock.Of<ITool>());
        var lookup = new FirstPartyToolLookup(services.BuildServiceProvider(), new HashSet<string>());

        lookup.Resolve("mcp_tool").Should().BeNull();
    }

    [Fact]
    public void Resolve_NameInBoundedSetButNotRegistered_ReturnsNull()
    {
        var services = new ServiceCollection();
        var lookup = new FirstPartyToolLookup(
            services.BuildServiceProvider(), new HashSet<string> { "unregistered_tool" });

        lookup.Resolve("unregistered_tool").Should().BeNull();
    }
}
