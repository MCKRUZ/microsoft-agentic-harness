using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Skills;
using Application.AI.Common.Services.Bundles;
using Domain.AI.Agents;
using Domain.AI.Bundles;
using Domain.AI.Skills;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.Services.Bundles;

/// <summary>
/// Tests the per-run overlay mechanism that lets a bundle's ephemeral agent and owned skills resolve
/// ahead of the persistent registries for the duration of one async flow, then vanish — proving both
/// the ambient accessor and the two overlay-aware decorators, without any HTTP or job machinery.
/// </summary>
public sealed class OverlayAwareResolutionTests
{
    // --- Ambient accessor ---------------------------------------------------------------------------

    [Fact]
    public void Accessor_Begin_PublishesOverlayThenRestoresOnDispose()
    {
        EphemeralAgentOverlayAccessor.Current.Should().BeNull("no overlay is active outside a run");

        var overlay = Overlay(Agent("ephem"));
        using (EphemeralAgentOverlayAccessor.Begin(overlay))
        {
            EphemeralAgentOverlayAccessor.Current.Should().BeSameAs(overlay);
        }

        EphemeralAgentOverlayAccessor.Current.Should().BeNull("the scope restored the previous value");
    }

    [Fact]
    public void Accessor_Begin_RestoresPreviousOverlay_NotJustNull()
    {
        var outer = Overlay(Agent("outer"));
        var inner = Overlay(Agent("inner"));

        using (EphemeralAgentOverlayAccessor.Begin(outer))
        {
            using (EphemeralAgentOverlayAccessor.Begin(inner))
                EphemeralAgentOverlayAccessor.Current.Should().BeSameAs(inner);

            EphemeralAgentOverlayAccessor.Current.Should().BeSameAs(outer, "the inner scope restored the outer overlay");
        }
    }

    // --- Owned-skill store decorator ----------------------------------------------------------------

    [Fact]
    public void OwnedStore_NoOverlay_ForwardsToInner()
    {
        var inner = new FakeOwnedSkillStore();
        inner.Register("host", Skill("hostskill", "HOST"));
        var store = new OverlayAwareAgentOwnedSkillStore(inner);

        store.TryGet("host", "hostskill")!.Description.Should().Be("HOST");
        store.TryGet("ephem", "bundleskill").Should().BeNull();
    }

    [Fact]
    public void OwnedStore_OverlayMatchingAgent_ResolvesOverlaySkillFirst()
    {
        var inner = new FakeOwnedSkillStore();
        inner.Register("host", Skill("hostskill", "HOST"));
        var store = new OverlayAwareAgentOwnedSkillStore(inner);

        using (EphemeralAgentOverlayAccessor.Begin(Overlay(Agent("ephem"), Skill("bundleskill", "OVERLAY"))))
        {
            store.TryGet("ephem", "bundleskill")!.Description.Should().Be("OVERLAY");
            // A different agent id is untouched by the overlay — still resolves from the inner store.
            store.TryGet("host", "hostskill")!.Description.Should().Be("HOST");
        }

        store.TryGet("ephem", "bundleskill").Should().BeNull("the overlay is gone after the scope");
    }

    [Fact]
    public void OwnedStore_Register_AlwaysWritesToInner_NeverTheOverlay()
    {
        var inner = new FakeOwnedSkillStore();
        var store = new OverlayAwareAgentOwnedSkillStore(inner);

        using (EphemeralAgentOverlayAccessor.Begin(Overlay(Agent("ephem"), Skill("bundleskill", "OVERLAY"))))
            store.Register("host", Skill("hostskill", "HOST"));

        // The write landed in the persistent store, not the ephemeral overlay.
        inner.TryGet("host", "hostskill")!.Description.Should().Be("HOST");
    }

    [Fact]
    public void OwnedStore_OverlayMatchedAgent_IsAuthoritative_DoesNotLeakInnerOwnedSkills()
    {
        // The bundle's ephemeral agent shares an id with a host agent that owns a private skill. The
        // overlay must fully own its agent's skill namespace, so the host's private skill is NOT visible.
        var inner = new FakeOwnedSkillStore();
        inner.Register("ephem", Skill("host-secret", "HOST"));
        var store = new OverlayAwareAgentOwnedSkillStore(inner);

        using (EphemeralAgentOverlayAccessor.Begin(Overlay(Agent("ephem"), Skill("bundle-only", "OVERLAY"))))
        {
            // A skill only the host agent owns must NOT resolve through the overlaid agent id...
            store.TryGet("ephem", "host-secret").Should()
                .BeNull("the overlay is authoritative for its agent; a miss falls back to the global pool, not host-owned skills");
            // ...and GetForAgent returns exactly the overlay's skills, never merged with the inner store.
            store.GetForAgent("ephem").Select(s => s.Id).Should().BeEquivalentTo(["bundle-only"]);
        }
    }

    // --- Agent metadata registry decorator ----------------------------------------------------------

    [Fact]
    public void AgentRegistry_NoOverlay_ForwardsToInner()
    {
        var registry = new OverlayAwareAgentMetadataRegistry(new FakeAgentRegistry(Agent("host")));

        registry.TryGet("host").Should().NotBeNull();
        registry.TryGet("ephem").Should().BeNull();
        registry.GetAll().Should().ContainSingle();
    }

    [Fact]
    public void AgentRegistry_OverlayAgent_IsResolvableAndEnumeratedForTheRun()
    {
        var registry = new OverlayAwareAgentMetadataRegistry(new FakeAgentRegistry(Agent("host")));

        using (EphemeralAgentOverlayAccessor.Begin(Overlay(Agent("ephem", "bundle-cat", "b-tag"))))
        {
            registry.TryGet("ephem").Should().NotBeNull();
            registry.TryGet("host").Should().NotBeNull("the persistent agent is still visible");
            registry.GetAll().Select(a => a.Id).Should().BeEquivalentTo(["host", "ephem"]);
            registry.GetByCategory("bundle-cat").Select(a => a.Id).Should().ContainSingle(id => id == "ephem");
            registry.GetByTags(["b-tag"]).Select(a => a.Id).Should().ContainSingle(id => id == "ephem");
        }

        registry.TryGet("ephem").Should().BeNull("the ephemeral agent is gone after the scope");
        registry.GetAll().Should().ContainSingle();
    }

    [Fact]
    public void AgentRegistry_OverlayShadowsSameIdAgent_AcrossEveryProjection()
    {
        // A persistent agent and the overlay share an id but differ in category/tags. For the run the
        // overlay fully replaces the persistent one: the stale definition must not surface under its old
        // category or tags, and the id must appear exactly once (as the overlay) in GetAll.
        var inner = new FakeAgentRegistry(Agent("dup", "research", "old-tag"));
        var registry = new OverlayAwareAgentMetadataRegistry(inner);

        using (EphemeralAgentOverlayAccessor.Begin(Overlay(Agent("dup", "analysis", "new-tag"))))
        {
            registry.TryGet("dup")!.Category.Should().Be("analysis", "the overlay definition wins");
            registry.GetByCategory("research").Should().BeEmpty("the shadowed persistent agent must not surface under its old category");
            registry.GetByCategory("analysis").Select(a => a.Id).Should().ContainSingle(id => id == "dup");
            registry.GetByTags(["old-tag"]).Should().BeEmpty("the shadowed persistent agent must not surface under its old tag");
            registry.GetByTags(["new-tag"]).Select(a => a.Id).Should().ContainSingle(id => id == "dup");
            registry.GetAll().Should().ContainSingle(a => a.Id == "dup", "the id appears once, as the overlay");
        }

        registry.GetByCategory("research").Select(a => a.Id).Should().ContainSingle(id => id == "dup",
            "after the scope the persistent agent is visible again under its own category");
    }

    // --- Helpers ------------------------------------------------------------------------------------

    private static AgentDefinition Agent(string id, string? category = null, params string[] tags) =>
        new() { Id = id, Name = id, Category = category, Tags = tags };

    private static SkillDefinition Skill(string id, string marker) =>
        new() { Id = id, Name = id, Description = marker };

    private static EphemeralAgentOverlay Overlay(AgentDefinition agent, params SkillDefinition[] skills) =>
        new() { Agent = agent, OwnedSkills = skills };

    private sealed class FakeOwnedSkillStore : IAgentOwnedSkillStore
    {
        private readonly Dictionary<(string, string), SkillDefinition> _skills = new();

        public void Register(string agentId, SkillDefinition skill) => _skills[(agentId, skill.Id)] = skill;

        public SkillDefinition? TryGet(string agentId, string skillId) =>
            _skills.TryGetValue((agentId, skillId), out var s) ? s : null;

        public IReadOnlyList<SkillDefinition> GetForAgent(string agentId) =>
            _skills.Where(kv => kv.Key.Item1 == agentId).Select(kv => kv.Value).ToList();
    }

    private sealed class FakeAgentRegistry : IAgentMetadataRegistry
    {
        private readonly List<AgentDefinition> _agents;

        public FakeAgentRegistry(params AgentDefinition[] agents) => _agents = [.. agents];

        public IReadOnlyList<AgentDefinition> GetAll() => _agents;

        public AgentDefinition? TryGet(string agentId) =>
            _agents.FirstOrDefault(a => string.Equals(a.Id, agentId, StringComparison.OrdinalIgnoreCase));

        public IReadOnlyList<AgentDefinition> GetByCategory(string category) =>
            _agents.Where(a => string.Equals(a.Category, category, StringComparison.OrdinalIgnoreCase)).ToList();

        public IReadOnlyList<AgentDefinition> GetByTags(IEnumerable<string> tags)
        {
            var set = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
            return _agents.Where(a => a.Tags.Any(set.Contains)).ToList();
        }

        public IReadOnlyList<string> SearchedPaths => [];
    }
}
