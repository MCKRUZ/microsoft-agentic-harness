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

        lookup.TryResolve("file_system", out _).Should().BeSameAs(tool);
    }

    [Fact]
    public void Resolve_NameOutsideBoundedSet_ReturnsNullWithoutProbingContainer()
    {
        // Registered in the container but NOT in the bounded key set — e.g. an MCP or bundle-owned
        // name. Must resolve to null without ever calling GetKeyedService for it.
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool>("mcp_tool", (_, _) => Mock.Of<ITool>());
        var lookup = new FirstPartyToolLookup(services.BuildServiceProvider(), new HashSet<string>());

        lookup.TryResolve("mcp_tool", out _).Should().BeNull();
    }

    [Fact]
    public void Resolve_NameInBoundedSetButNotRegistered_ReturnsNull()
    {
        var services = new ServiceCollection();
        var lookup = new FirstPartyToolLookup(
            services.BuildServiceProvider(), new HashSet<string> { "unregistered_tool" });

        lookup.TryResolve("unregistered_tool", out _).Should().BeNull();
    }

    [Fact]
    public void Resolve_CallerSuppliesDifferentCasingThanRegistrationKey_StillResolvesTheTool()
    {
        // #655: an operator-authored grant/deny entry can differ from the actual DI registration key
        // only in casing. GetKeyedService resolves by exact key, so this only works if Resolve probes
        // DI with the CANONICAL ("bash") casing recovered from the bounded set, never the caller's
        // raw ("BASH") casing.
        var tool = Mock.Of<ITool>();
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool>("bash", (_, _) => tool);
        var lookup = new FirstPartyToolLookup(
            services.BuildServiceProvider(), new HashSet<string> { "bash" });

        lookup.TryResolve("BASH", out _).Should().BeSameAs(tool);
        lookup.TryResolve("Bash", out _).Should().BeSameAs(tool);
    }

    [Fact]
    public void TryResolvePublishedName_CallerSuppliesDifferentCasingThanRegistrationKey_ResolvesPublishedName()
    {
        var tool = Mock.Of<ITool>(t => t.Name == "bash");
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool>("bash", (_, _) => tool);
        var lookup = new FirstPartyToolLookup(
            services.BuildServiceProvider(), new HashSet<string> { "bash" });

        var resolved = lookup.TryResolvePublishedName("BASH", out var publishedName, out var constructionError);

        resolved.Should().BeTrue();
        publishedName.Should().Be("bash");
        constructionError.Should().BeNull();
    }

    [Fact]
    public void Constructor_TwoRegistrationKeysDifferOnlyByCase_Throws()
    {
        var services = new ServiceCollection();

        var act = () => new FirstPartyToolLookup(
            services.BuildServiceProvider(), new HashSet<string> { "bash", "BASH" });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*differ only by case*");
    }
}
