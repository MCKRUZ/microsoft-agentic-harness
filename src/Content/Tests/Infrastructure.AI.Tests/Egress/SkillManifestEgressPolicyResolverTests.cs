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
        using var _ = accessor.BeginScope(["github-reader"]);

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
    /// #589: two skills active at once (the shared-tool-name union case) resolve a policy carrying
    /// BOTH skills' own allowlist additions, not just the first or the default. Proves the resolver's
    /// own multi-id path, independent of how <c>ToolChainBuilder</c>/<c>GovernedAIFunction</c> come to
    /// establish more than one id at a time.
    /// </summary>
    [Fact]
    public async Task ResolveFor_TwoSkillsActive_UnionsBothSkillsOwnAllowlists()
    {
        var accessor = new CurrentSkillAccessor();
        using var _ = accessor.BeginScope(["skill-a", "skill-b"]);

        var skillA = SkillWithAllowlist("skill-a", new EgressAllowlistEntry
        {
            Host = "a.example.com", Schemes = ["https"], Ports = [443]
        });
        var skillB = SkillWithAllowlist("skill-b", new EgressAllowlistEntry
        {
            Host = "b.example.com", Schemes = ["https"], Ports = [443]
        });

        var registry = new Mock<ISkillMetadataRegistry>(MockBehavior.Strict);
        registry.Setup(r => r.TryGet("skill-a")).Returns(skillA);
        registry.Setup(r => r.TryGet("skill-b")).Returns(skillB);

        var resolver = NewResolver(accessor, registry.Object,
            new EgressAllowlistConfigEntry { Host = "default.example.com", Schemes = ["https"], Ports = [443] });

        var policy = resolver.ResolveFor(TestIdentity.Default);

        var aVerdict = await policy.AllowAsync(new Uri("https://a.example.com/"), TestIdentity.Default, CancellationToken.None);
        aVerdict.Allowed.Should().BeTrue("skill-a's own allowlist addition must apply even though skill-b is also active");

        var bVerdict = await policy.AllowAsync(new Uri("https://b.example.com/"), TestIdentity.Default, CancellationToken.None);
        bVerdict.Allowed.Should().BeTrue("skill-b's own allowlist addition must apply even though skill-a is also active");

        var defaultVerdict = await policy.AllowAsync(new Uri("https://default.example.com/"), TestIdentity.Default, CancellationToken.None);
        defaultVerdict.Allowed.Should().BeTrue("the harness-wide default must still apply under a multi-skill scope");

        var deniedVerdict = await policy.AllowAsync(new Uri("https://attacker.example.org/"), TestIdentity.Default, CancellationToken.None);
        deniedVerdict.Allowed.Should().BeFalse("a host neither skill nor the default names must still be refused");
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
        using var _ = accessor.BeginScope(["cached-skill"]);

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
    /// No skill in scope (<see cref="ICurrentSkillAccessor.CurrentSkillIds"/> is
    /// empty) falls back to a default-only policy. The resolver does not touch
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
        using var _ = accessor.BeginScope(["unknown-skill"]);

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
        using var _ = accessor.BeginScope(["empty-allowlist"]);

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
    /// Security-review finding on #531: a skill literally named a case variant of the resolver's
    /// former reserved sentinel string must resolve its OWN policy, never collide with the no-skill
    /// default — proving the fix (separate cache/field for the two cases) rather than relying on the
    /// sentinel string never being reused by a real skill id.
    /// </summary>
    [Fact]
    public async Task ResolveFor_SkillNamedLikeTheOldSentinel_ResolvesItsOwnPolicy_NotTheDefault()
    {
        var accessor = new CurrentSkillAccessor();
        var skill = SkillWithAllowlist("<NO-SKILL>", new EgressAllowlistEntry
        {
            Host = "widened.example.com",
            Schemes = ["https"],
            Ports = [443]
        });

        var registry = new Mock<ISkillMetadataRegistry>(MockBehavior.Strict);
        registry.Setup(r => r.TryGet("<NO-SKILL>")).Returns(skill);

        var resolver = NewResolver(accessor, registry.Object,
            new EgressAllowlistConfigEntry { Host = "default.example.com", Schemes = ["https"], Ports = [443] });

        // No-skill policy resolved first, so any shared-cache collision would already have poisoned
        // the entry the skill lookup below reads.
        var noSkillPolicy = resolver.ResolveFor(TestIdentity.Default);
        var noSkillVerdict = await noSkillPolicy.AllowAsync(
            new Uri("https://widened.example.com/"), TestIdentity.Default, CancellationToken.None);
        noSkillVerdict.Allowed.Should().BeFalse("the no-skill policy must never carry a skill's widened allowlist");

        using var _ = accessor.BeginScope(["<NO-SKILL>"]);
        var skillPolicy = resolver.ResolveFor(TestIdentity.Default);
        var skillVerdict = await skillPolicy.AllowAsync(
            new Uri("https://widened.example.com/"), TestIdentity.Default, CancellationToken.None);
        skillVerdict.Allowed.Should().BeTrue("the skill's own allowlist addition must still apply for its own scope");
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
        accessor.CurrentSkillIds.Should().BeEmpty();

        using (var outer = accessor.BeginScope(["outer"]))
        {
            accessor.CurrentSkillIds.Should().Equal("outer");

            using (var inner = accessor.BeginScope(["inner"]))
            {
                accessor.CurrentSkillIds.Should().Equal("inner");
            }

            accessor.CurrentSkillIds.Should().Equal("outer");
        }

        accessor.CurrentSkillIds.Should().BeEmpty();
    }
}
