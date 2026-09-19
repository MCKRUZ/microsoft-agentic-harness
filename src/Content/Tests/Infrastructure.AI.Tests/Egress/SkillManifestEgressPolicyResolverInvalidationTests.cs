using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Egress;
using Domain.AI.Egress;
using Domain.AI.Skills;
using Infrastructure.AI.Egress;
using Infrastructure.AI.Skills;
using Infrastructure.AI.Tests.Egress.Support;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Egress;

/// <summary>
/// Proves the fix for the security-review finding on issue #709: before this, a skill's cached
/// egress policy was permanent — nothing ever invalidated it, so an operator narrowing or revoking a
/// skill's allowed hosts via a hot-reloaded <c>SKILL.md</c> would keep having the OLD, broader policy
/// enforced until process restart, silently defeating the exact security fix they believed they had
/// just applied. <see cref="SkillManifestEgressPolicyResolver"/> now clears its per-skill cache
/// whenever <see cref="ISkillMetadataRegistry.Version"/> advances.
/// </summary>
public sealed class SkillManifestEgressPolicyResolverInvalidationTests
{
    private static SkillDefinition SkillWithAllowlist(string id, params EgressAllowlistEntry[] entries) => new()
    {
        Id = id,
        Name = id,
        Egress = new EgressManifest { Allowlist = entries }
    };

    [Fact]
    public async Task ResolveFor_SkillRegistryVersionAdvances_DropsCachedPolicyAndRebuildsFromCurrentSkill()
    {
        var accessor = new CurrentSkillAccessor();
        using var _ = accessor.BeginScope(["reporter"]);

        var registry = new Mock<ISkillMetadataRegistry>(MockBehavior.Strict);
        var version = 1L;
        registry.SetupGet(r => r.Version).Returns(() => version);

        // Version 1: the skill is allowed to reach a since-compromised host.
        registry.Setup(r => r.TryGet("reporter")).Returns(SkillWithAllowlist("reporter", new EgressAllowlistEntry
        {
            Host = "compromised.example.com",
            Schemes = ["https"],
            Ports = [443]
        }));

        var (monitor, _) = TestConfig.NewMonitor();
        var resolver = new SkillManifestEgressPolicyResolver(
            accessor,
            registry.Object,
            monitor,
            NullLogger<SkillManifestEgressPolicyResolver>.Instance,
            NullLogger<DefaultEgressPolicy>.Instance,
            TimeProvider.System);

        var beforeRevocation = resolver.ResolveFor(TestIdentity.Default);
        var stillAllowedBeforeRevocation = await beforeRevocation.AllowAsync(
            new Uri("https://compromised.example.com/exfiltrate"), TestIdentity.Default, CancellationToken.None);
        stillAllowedBeforeRevocation.Allowed.Should().BeTrue("the host is legitimately allowed before revocation");

        // The operator edits SKILL.md to remove the compromised host, and the skill registry
        // reloads (hot-reload from #709) — simulated here by bumping Version and changing what
        // TryGet returns, exactly as a real reload would leave the registry afterward.
        version = 2;
        registry.Setup(r => r.TryGet("reporter")).Returns(SkillWithAllowlist("reporter"));

        var afterRevocation = resolver.ResolveFor(TestIdentity.Default);
        var stillAllowedAfterRevocation = await afterRevocation.AllowAsync(
            new Uri("https://compromised.example.com/exfiltrate"), TestIdentity.Default, CancellationToken.None);

        stillAllowedAfterRevocation.Allowed.Should().BeFalse(
            "the resolver must drop its cached policy and rebuild from the reloaded skill once the " +
            "registry's Version advances — serving the old, broader policy after a revocation is " +
            "the exact defect this fix closes");
    }

    [Fact]
    public async Task ResolveFor_SkillRegistryVersionUnchanged_KeepsServingTheSameCachedPolicyInstance()
    {
        // The inverse of the above: an invalidation check that fires on every call for no reason
        // would defeat the whole point of caching. Proves the cache survives across calls when
        // nothing changed.
        var accessor = new CurrentSkillAccessor();
        using var _ = accessor.BeginScope(["stable-skill"]);

        var registry = new Mock<ISkillMetadataRegistry>(MockBehavior.Strict);
        registry.SetupGet(r => r.Version).Returns(1L);
        registry.Setup(r => r.TryGet("stable-skill")).Returns(SkillWithAllowlist("stable-skill", new EgressAllowlistEntry
        {
            Host = "api.example.com",
            Schemes = ["https"],
            Ports = [443]
        }));

        var (monitor, _) = TestConfig.NewMonitor();
        var resolver = new SkillManifestEgressPolicyResolver(
            accessor,
            registry.Object,
            monitor,
            NullLogger<SkillManifestEgressPolicyResolver>.Instance,
            NullLogger<DefaultEgressPolicy>.Instance,
            TimeProvider.System);

        var first = resolver.ResolveFor(TestIdentity.Default);
        var second = resolver.ResolveFor(TestIdentity.Default);

        second.Should().BeSameAs(first, "an unchanged Version must not invalidate the per-skill cache");
    }
}
