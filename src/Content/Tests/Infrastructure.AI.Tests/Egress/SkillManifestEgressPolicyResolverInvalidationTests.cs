using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Egress;
using Application.AI.Common.Interfaces.Skills;
using Domain.AI.Egress;
using Domain.AI.Skills;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Infrastructure.AI.Egress;
using Infrastructure.AI.Skills;
using Infrastructure.AI.Tests.Egress.Support;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Tests.Common;
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

    /// <summary>
    /// The exact scenario CI's correctness-review gate found missing: this drives a REAL
    /// <see cref="SkillMetadataRegistry"/> through its real <c>Invalidate()</c> — the same call the
    /// automatic <c>SkillManifestWatcherService</c> makes — rather than a mock whose <c>Version</c> is
    /// bumped by hand. The unit tests above (using a mocked registry) could not have caught the actual
    /// bug: <c>Invalidate()</c> alone didn't advance <c>Version</c>, so a Version-checking consumer's
    /// cache never noticed the reload at all, because it specifically avoids calling back into the
    /// registry on what it believes is still a cache hit.
    /// </summary>
    [Fact]
    public async Task ResolveFor_RealRegistryInvalidated_DropsCachedPolicyAndRebuildsFromCurrentSkill()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"egress-real-invalidate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempRoot, "reporter"));
        var skillFile = Path.Combine(tempRoot, "reporter", "SKILL.md");
        try
        {
            File.WriteAllText(skillFile, """
                ---
                name: "reporter"
                egress:
                  allowlist:
                    - host: "compromised.example.com"
                      schemes: ["https"]
                      ports: [443]
                ---
                Body.
                """);

            var fileReader = new UnsandboxedSkillFileReader();
            var registry = new SkillMetadataRegistry(
                NullLogger<SkillMetadataRegistry>.Instance,
                new StaticOptionsMonitor(new AppConfig
                {
                    AI = new AIConfig { Skills = new SkillsConfig { BasePath = tempRoot } }
                }),
                new SkillMetadataParser(
                    NullLogger<SkillMetadataParser>.Instance, fileReader,
                    TestMcpSecurityScanner.AlwaysSafe(), TestMcpSecurityScanner.DefaultConfig(),
                    TestMcpSecurityScanner.RealEgressValidator()),
                fileReader);

            // Force the registry's first (lazy) load to happen NOW, before the resolver ever reads
            // Version — otherwise the resolver's own first ResolveFor call would trigger that same
            // first load as a side effect of a cache miss, and THAT load's own Version bump would
            // mask whether Invalidate() itself needs to bump Version, which is exactly what this test
            // exists to isolate.
            registry.GetAll();

            var accessor = new CurrentSkillAccessor();
            using var _ = accessor.BeginScope(["reporter"]);

            var (monitor, _) = TestConfig.NewMonitor();
            var resolver = new SkillManifestEgressPolicyResolver(
                accessor,
                registry,
                monitor,
                NullLogger<SkillManifestEgressPolicyResolver>.Instance,
                NullLogger<DefaultEgressPolicy>.Instance,
                TimeProvider.System);

            var beforeRevocation = resolver.ResolveFor(TestIdentity.Default);
            var stillAllowedBeforeRevocation = await beforeRevocation.AllowAsync(
                new Uri("https://compromised.example.com/exfiltrate"), TestIdentity.Default, CancellationToken.None);
            stillAllowedBeforeRevocation.Allowed.Should().BeTrue("the host is legitimately allowed before revocation");

            // The operator edits SKILL.md to remove the compromised host, then the real automatic
            // path — the watcher's Invalidate() call, exactly reproduced here — fires.
            File.WriteAllText(skillFile, """
                ---
                name: "reporter"
                ---
                Body.
                """);
            registry.Invalidate();

            var afterRevocation = resolver.ResolveFor(TestIdentity.Default);
            var stillAllowedAfterRevocation = await afterRevocation.AllowAsync(
                new Uri("https://compromised.example.com/exfiltrate"), TestIdentity.Default, CancellationToken.None);

            stillAllowedAfterRevocation.Allowed.Should().BeFalse(
                "a real Invalidate() call — the automatic watcher path — must propagate to a " +
                "Version-checking consumer's cache even though nothing else ever reads the registry " +
                "directly; this is the gap a mocked Version sequence could not expose");
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private sealed class StaticOptionsMonitor : IOptionsMonitor<AppConfig>
    {
        public StaticOptionsMonitor(AppConfig value) => CurrentValue = value;
        public AppConfig CurrentValue { get; }
        public AppConfig Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AppConfig, string?> listener) => null;
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
