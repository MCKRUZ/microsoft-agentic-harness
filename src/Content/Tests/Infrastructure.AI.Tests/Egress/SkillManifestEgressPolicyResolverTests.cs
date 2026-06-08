using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Egress;
using Application.AI.Common.Interfaces.Skills;
using Domain.AI.Egress;
using Domain.AI.Skills;
using Domain.Common.Config.AI;
using FluentAssertions;
using Infrastructure.AI.Egress;
using Infrastructure.AI.Skills;
using Infrastructure.AI.Tests.Egress.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Egress;

/// <summary>
/// PR-3c: per-skill policy resolver tests. Covers (a) additive merge of the
/// harness-wide default with per-skill allowlists, (b) per-skill cache
/// stability (same id → same instance), and (c) fall-back behavior when no
/// skill or an unknown skill is in scope.
/// </summary>
public sealed class SkillManifestEgressPolicyResolverTests
{
    private static SkillDefinition SkillWithAllowlist(string id, params EgressAllowlistEntry[] entries)
    {
        return new SkillDefinition
        {
            Id = id,
            Name = id,
            Egress = entries.Length == 0
                ? new EgressManifest { Allowlist = [] }
                : new EgressManifest { Allowlist = entries }
        };
    }

    private static SkillManifestEgressPolicyResolver NewResolver(
        ICurrentSkillAccessor currentSkill,
        ISkillMetadataRegistry skillRegistry,
        params EgressAllowlistConfigEntry[] defaultAllowlist)
    {
        var (monitor, _) = TestConfig.NewMonitor(defaultAllowlist);
        return new SkillManifestEgressPolicyResolver(
            currentSkill,
            skillRegistry,
            monitor,
            NullLogger<SkillManifestEgressPolicyResolver>.Instance,
            NullLogger<DefaultEgressPolicy>.Instance,
            TimeProvider.System);
    }

    /// <summary>
    /// Test 5 (brief): default + per-skill allowlist merge is a UNION. A request
    /// matching either the default OR the per-skill entry is allowed.
    /// </summary>
    [Fact]
    public async Task ResolveFor_SkillWithAllowlist_MergesDefaultAndPerSkill()
    {
        var accessor = new CurrentSkillAccessor();
        using var _ = accessor.BeginScope("github-reader");

        var skill = SkillWithAllowlist("github-reader", new EgressAllowlistEntry
        {
            Host = "api.github.com",
            Schemes = ["https"],
            Ports = [443]
        });

        var registry = new Mock<ISkillMetadataRegistry>(MockBehavior.Strict);
        registry.Setup(r => r.TryGet("github-reader")).Returns(skill);

        var resolver = NewResolver(accessor, registry.Object,
            new EgressAllowlistConfigEntry
            {
                Host = "default.example.com",
                Schemes = ["https"],
                Ports = [443]
            });

        var policy = resolver.ResolveFor(TestIdentity.Default);

        // Per-skill entry passes.
        var perSkillVerdict = await policy.AllowAsync(
            new Uri("https://api.github.com/issues"),
            TestIdentity.Default,
            CancellationToken.None);
        perSkillVerdict.Allowed.Should().BeTrue();
        perSkillVerdict.MatchedAllowlistEntry.Should().Be("api.github.com");

        // Default entry passes too — proves the merge is a union, not a replace.
        var defaultVerdict = await policy.AllowAsync(
            new Uri("https://default.example.com/anything"),
            TestIdentity.Default,
            CancellationToken.None);
        defaultVerdict.Allowed.Should().BeTrue();
        defaultVerdict.MatchedAllowlistEntry.Should().Be("default.example.com");
    }

    /// <summary>
    /// Test 6 (brief): the resolver caches by skill key. Two lookups with the
    /// same active skill return the SAME policy instance. Per-skill cache keeps
    /// the merge cost amortized over the lifetime of the process.
    /// </summary>
    [Fact]
    public void ResolveFor_SameSkillTwice_ReturnsSamePolicyInstance()
    {
        var accessor = new CurrentSkillAccessor();
        using var _ = accessor.BeginScope("cached-skill");

        var skill = SkillWithAllowlist("cached-skill", new EgressAllowlistEntry
        {
            Host = "cache.example.com",
            Schemes = ["https"],
            Ports = [443]
        });

        var registry = new Mock<ISkillMetadataRegistry>(MockBehavior.Strict);
        // Setup once; assert the resolver doesn't ask twice.
        registry.Setup(r => r.TryGet("cached-skill")).Returns(skill);

        var resolver = NewResolver(accessor, registry.Object);

        var first = resolver.ResolveFor(TestIdentity.Default);
        var second = resolver.ResolveFor(TestIdentity.Default);

        first.Should().BeSameAs(second);
        registry.Verify(r => r.TryGet("cached-skill"), Times.Once,
            "the cache should serve the second lookup without going back to the registry");
    }

    /// <summary>
    /// No skill in scope (<see cref="ICurrentSkillAccessor.CurrentSkillId"/> is
    /// null) falls back to a default-only policy. The resolver does not touch
    /// the registry on the no-skill path.
    /// </summary>
    [Fact]
    public async Task ResolveFor_NoSkillActive_FallsBackToDefaultOnlyPolicy()
    {
        var accessor = new CurrentSkillAccessor(); // no BeginScope — null current
        var registry = new Mock<ISkillMetadataRegistry>(MockBehavior.Strict);

        var resolver = NewResolver(accessor, registry.Object,
            new EgressAllowlistConfigEntry
            {
                Host = "default.example.com",
                Schemes = ["https"],
                Ports = [443]
            });

        var policy = resolver.ResolveFor(TestIdentity.Default);

        var verdict = await policy.AllowAsync(
            new Uri("https://default.example.com/anything"),
            TestIdentity.Default,
            CancellationToken.None);
        verdict.Allowed.Should().BeTrue();

        registry.Verify(r => r.TryGet(It.IsAny<string>()), Times.Never,
            "the no-skill path must not consult the skill registry");
    }

    /// <summary>
    /// An unknown skill in scope (registry returns null) falls back to a
    /// default-only policy and logs a warning. Tests should not crash when a
    /// stale skill identifier survives a registry refresh.
    /// </summary>
    [Fact]
    public async Task ResolveFor_UnknownSkill_FallsBackToDefaultOnlyPolicy()
    {
        var accessor = new CurrentSkillAccessor();
        using var _ = accessor.BeginScope("unknown-skill");

        var registry = new Mock<ISkillMetadataRegistry>(MockBehavior.Strict);
        registry.Setup(r => r.TryGet("unknown-skill")).Returns((SkillDefinition?)null);

        var resolver = NewResolver(accessor, registry.Object,
            new EgressAllowlistConfigEntry
            {
                Host = "default.example.com",
                Schemes = ["https"],
                Ports = [443]
            });

        var policy = resolver.ResolveFor(TestIdentity.Default);

        var verdict = await policy.AllowAsync(
            new Uri("https://default.example.com/anything"),
            TestIdentity.Default,
            CancellationToken.None);
        verdict.Allowed.Should().BeTrue();
    }

    /// <summary>
    /// A skill with an explicit empty allowlist behaves identically to a skill
    /// without an egress block — the harness-wide default is returned with no
    /// additions.
    /// </summary>
    [Fact]
    public async Task ResolveFor_SkillWithEmptyAllowlist_ReturnsDefaultOnly()
    {
        var accessor = new CurrentSkillAccessor();
        using var _ = accessor.BeginScope("empty-allowlist");

        var registry = new Mock<ISkillMetadataRegistry>(MockBehavior.Strict);
        registry.Setup(r => r.TryGet("empty-allowlist"))
            .Returns(SkillWithAllowlist("empty-allowlist"));

        var resolver = NewResolver(accessor, registry.Object,
            new EgressAllowlistConfigEntry
            {
                Host = "default.example.com",
                Schemes = ["https"],
                Ports = [443]
            });

        var policy = resolver.ResolveFor(TestIdentity.Default);

        var verdict = await policy.AllowAsync(
            new Uri("https://default.example.com/anything"),
            TestIdentity.Default,
            CancellationToken.None);
        verdict.Allowed.Should().BeTrue();

        var denied = await policy.AllowAsync(
            new Uri("https://attacker.example.org/"),
            TestIdentity.Default,
            CancellationToken.None);
        denied.Allowed.Should().BeFalse();
    }

    /// <summary>
    /// CurrentSkillAccessor: nested BeginScope() composes — the inner activation
    /// restores the previous skill id on dispose. Guards against a stale
    /// identifier leaking across logical scopes.
    /// </summary>
    [Fact]
    public void CurrentSkillAccessor_NestedScopes_RestorePreviousOnDispose()
    {
        var accessor = new CurrentSkillAccessor();
        accessor.CurrentSkillId.Should().BeNull();

        using (var outer = accessor.BeginScope("outer"))
        {
            accessor.CurrentSkillId.Should().Be("outer");

            using (var inner = accessor.BeginScope("inner"))
            {
                accessor.CurrentSkillId.Should().Be("inner");
            }

            accessor.CurrentSkillId.Should().Be("outer");
        }

        accessor.CurrentSkillId.Should().BeNull();
    }
}
