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

    // --- #651: a successful published-name resolution is memoized for the process lifetime, so the
    // permission-rule providers that call this on every tool-permission resolution stop paying a DI
    // probe per name per call. The two NEGATIVE cases below are the safety half of that memo and matter
    // more than the positive one: memoizing an unknown name would let a caller grow the dictionary
    // without bound (MCP names embed a per-run bundle id), and memoizing a construction failure would
    // permanently downgrade a security control on the strength of one transient fault.

    /// <summary>
    /// Builds a lookup whose tool records every member access, so a test can count reads of
    /// <see cref="ITool.Name"/> — the observation that actually distinguishes a memo hit from a live
    /// resolution. Counting how often the DI factory runs does NOT: the tool is a keyed SINGLETON, so
    /// the container builds it once whether or not this type memoizes anything, and a test asserting
    /// "constructed once" passes identically with the memo deleted (caught by mutation-testing these
    /// very tests — an unexpected pass).
    /// </summary>
    private static (FirstPartyToolLookup Lookup, Mock<ITool> Tool) LookupWithRecordingTool(
        string key, string publishedName)
    {
        var tool = new Mock<ITool>();
        tool.Setup(t => t.Name).Returns(publishedName);
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool>(key, (_, _) => tool.Object);

        return (new FirstPartyToolLookup(services.BuildServiceProvider(), new HashSet<string> { key }), tool);
    }

    [Fact]
    public void TryResolvePublishedName_RepeatedCalls_ReadsTheToolsNameOnlyOnce()
    {
        var (lookup, tool) = LookupWithRecordingTool("registered_key", "self_reported_name");

        lookup.TryResolvePublishedName("registered_key", out var first, out _).Should().BeTrue();
        var readsAfterFirstCall = tool.Invocations.Count;
        lookup.TryResolvePublishedName("registered_key", out var second, out _).Should().BeTrue();
        lookup.TryResolvePublishedName("registered_key", out var third, out _).Should().BeTrue();

        readsAfterFirstCall.Should().BeGreaterThan(0, "the first call must actually resolve the name");
        tool.Invocations.Count.Should().Be(readsAfterFirstCall,
            "the mapping cannot change, so later calls must be served from the memo");
        first.Should().Be("self_reported_name");
        second.Should().Be(first);
        third.Should().Be(first);
    }

    [Fact]
    public void TryResolvePublishedName_CasingVariantOfAMemoizedKey_HitsTheSameEntry()
    {
        // The memo must share this type's own case-insensitive resolution semantics (#655) — otherwise
        // every casing an operator happens to author gets its own entry and its own live resolution.
        var (lookup, tool) = LookupWithRecordingTool("bash", "bash");

        lookup.TryResolvePublishedName("bash", out _, out _).Should().BeTrue();
        var readsAfterFirstCall = tool.Invocations.Count;
        lookup.TryResolvePublishedName("BASH", out var published, out _).Should().BeTrue();

        tool.Invocations.Count.Should().Be(readsAfterFirstCall,
            "a casing variant of an already-memoized key must hit the same entry");
        published.Should().Be("bash");
    }

    [Fact]
    public void TryResolvePublishedName_NameOutsideBoundedSet_NeverBecomesAMemoHit()
    {
        // Callers pass unbounded, caller-authored names here (an MCP tool name embeds a per-run bundle
        // id). Memoizing the miss would reintroduce the unbounded process-lifetime growth the bounded
        // key set exists to prevent.
        //
        // Scope of this test, stated honestly: it pins that a miss never starts reporting SUCCESS on a
        // later call, which is what writing the fallback name into the memo dictionary would cause —
        // the realistic regression, and the one mutation-testing this test actually reproduces. It
        // does NOT prove the absence of some other negative-cache structure that keeps returning
        // false; nothing observable from outside this type could. The memo dictionary's contents are
        // private, so the invariant is carried by _publishedNameByKey's own remarks and by this
        // behavioural floor together.
        var services = new ServiceCollection();
        var lookup = new FirstPartyToolLookup(services.BuildServiceProvider(), new HashSet<string>());

        lookup.TryResolvePublishedName("mcp:run-123:tool", out var first, out _).Should().BeFalse();
        lookup.TryResolvePublishedName("mcp:run-123:tool", out var second, out _)
            .Should().BeFalse("a name outside the bounded set must never resolve, however often it is asked");

        first.Should().Be("mcp:run-123:tool", "an unresolved name falls back to the key itself");
        second.Should().Be(first);
    }

    [Fact]
    public void TryResolvePublishedName_NullKey_ReturnsFalseInsteadOfThrowing()
    {
        // A null key is reachable from operator-authored config: PluginPermissionRuleProvider forwards
        // every PluginDeclaration.DeniedTools entry unfiltered, and that list is bound from JSON where
        // ["bash", null] yields a null element. ThreePhasePermissionResolver does not catch provider
        // exceptions, so a throw here would take down permission resolution for every tool call in the
        // host — which is what an unguarded ConcurrentDictionary memo probe (it throws on a null key)
        // would have introduced.
        var services = new ServiceCollection();
        var lookup = new FirstPartyToolLookup(
            services.BuildServiceProvider(), new HashSet<string> { "bash" });

        var act = () => lookup.TryResolvePublishedName(null!, out _, out _);

        act.Should().NotThrow("the documented contract is to return false, never to throw");
        lookup.TryResolvePublishedName(null!, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryResolvePublishedName_ConstructionFailure_IsNotMemoized_AndLaterSuccessResolves()
    {
        // A construction failure is the one genuinely transient outcome here. Caching it would leave the
        // caller on key-only coverage — a permanently degraded security control — for the rest of the
        // process, so the next call must retry and pick up a tool that can now be built.
        var attempts = 0;
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITool>("flaky_tool", (_, _) =>
        {
            attempts++;
            if (attempts == 1)
                throw new InvalidOperationException("dependency not wired yet");
            return Mock.Of<ITool>(t => t.Name == "flaky_published_name");
        });
        var lookup = new FirstPartyToolLookup(
            services.BuildServiceProvider(), new HashSet<string> { "flaky_tool" });

        lookup.TryResolvePublishedName("flaky_tool", out var failedName, out var constructionError)
            .Should().BeFalse();
        failedName.Should().Be("flaky_tool");
        constructionError.Should().BeOfType<InvalidOperationException>();

        lookup.TryResolvePublishedName("flaky_tool", out var retriedName, out var retryError)
            .Should().BeTrue("the failure must not have been memoized");
        retriedName.Should().Be("flaky_published_name");
        retryError.Should().BeNull();
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
