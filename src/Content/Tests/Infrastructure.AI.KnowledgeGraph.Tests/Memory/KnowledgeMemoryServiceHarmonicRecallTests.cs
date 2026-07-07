using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.HarmonicMemory;
using Domain.Common.Config.AI.RAG;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.InMemory;
using Infrastructure.AI.KnowledgeGraph.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Memory;

/// <summary>
/// Tests for the harmonic memory (Memora port) recall path of <see cref="KnowledgeMemoryService"/>: matching
/// the query against the primary abstraction + cue anchors, shared-cue-anchor cluster traversal, RRF fusion
/// with the legacy substring/graph path, and preservation of the trust + scope-isolation guarantees on read.
/// </summary>
public sealed class KnowledgeMemoryServiceHarmonicRecallTests
{
    private readonly InMemoryGraphStore _graphStore = new(NullLogger<InMemoryGraphStore>.Instance);

    private const string DefaultNs = "memory:default:anon";

    [Fact]
    public async Task CueAnchorHit_HarmonicSurfacesNode_LegacyMisses()
    {
        // The node's name/type carry nothing about "vegetarian" — only its cue anchor does. The legacy
        // substring path (Off) matches name/type and misses it; harmonic recall matches the cue anchor.
        await SeedMemoryNodeAsync($"{DefaultNs}:pref-1", "pref-1", "The user is vegetarian",
            new MemoryAbstraction { Abstraction = "user dietary preference", CueAnchors = ["vegetarian diet"] });

        var offResult = await CreateService(ConfigWith(HarmonicMemoryMode.Off)).RecallAsync("vegetarian");
        offResult.Should().BeEmpty("the legacy path matches only node name/type, which carry no cue anchor");

        var harmonicResult = await CreateService(ConfigWith(HarmonicMemoryMode.AbstractOnly)).RecallAsync("vegetarian");
        harmonicResult.Should().ContainSingle(n => n.Id == $"{DefaultNs}:pref-1",
            "harmonic recall matches the query against the indexed cue anchors the legacy path ignores");
    }

    [Fact]
    public async Task AbstractionHit_HarmonicSurfacesNode()
    {
        await SeedMemoryNodeAsync($"{DefaultNs}:orion-1", "orion-1", "Milestone one shipped",
            new MemoryAbstraction { Abstraction = "Project Orion timeline" });

        var result = await CreateService(ConfigWith(HarmonicMemoryMode.AbstractOnly)).RecallAsync("timeline");

        result.Should().ContainSingle(n => n.Id == $"{DefaultNs}:orion-1",
            "the query token overlaps the primary abstraction");
    }

    [Fact]
    public async Task SharedCueAnchor_Traversal_ReturnsCoherentCluster()
    {
        // A directly matches the query; B does not, but shares a cue anchor with A, so the shared-anchor
        // traversal pulls B into the returned cluster.
        await SeedMemoryNodeAsync($"{DefaultNs}:a", "a", "timeline notes",
            new MemoryAbstraction { Abstraction = "Project Orion timeline", CueAnchors = ["Orion project"] });
        await SeedMemoryNodeAsync($"{DefaultNs}:b", "b", "budget figures",
            new MemoryAbstraction { Abstraction = "quarterly budget", CueAnchors = ["Orion project"] });

        var result = await CreateService(ConfigWith(HarmonicMemoryMode.Full)).RecallAsync("timeline");

        result.Should().Contain(n => n.Id == $"{DefaultNs}:a", "A is a direct abstraction hit");
        result.Should().Contain(n => n.Id == $"{DefaultNs}:b",
            "B shares the 'Orion project' cue anchor with A, so traversal surfaces the cluster");
    }

    [Fact]
    public async Task Traversal_FanoutZero_ReturnsOnlyDirectMatches()
    {
        await SeedMemoryNodeAsync($"{DefaultNs}:a", "a", "timeline notes",
            new MemoryAbstraction { Abstraction = "Project Orion timeline", CueAnchors = ["Orion project"] });
        await SeedMemoryNodeAsync($"{DefaultNs}:b", "b", "budget figures",
            new MemoryAbstraction { Abstraction = "quarterly budget", CueAnchors = ["Orion project"] });

        var result = await CreateService(ConfigWith(HarmonicMemoryMode.Full, fanout: 0)).RecallAsync("timeline");

        result.Should().Contain(n => n.Id == $"{DefaultNs}:a");
        result.Should().NotContain(n => n.Id == $"{DefaultNs}:b",
            "fanout 0 disables shared-anchor traversal, so only the direct match is returned");
    }

    [Fact]
    public async Task RrfFusion_CombinesHarmonicAndLegacyHits()
    {
        // X is found only by the legacy path (its key equals the query term); Y is found only by harmonic
        // (its cue anchor carries the query token). Fusion must return both.
        await SeedMemoryNodeAsync($"{DefaultNs}:orion", "orion", "the orion fact",
            new MemoryAbstraction { Abstraction = "cloud infrastructure" });
        await SeedMemoryNodeAsync($"{DefaultNs}:fan", "fan", "a fan note",
            new MemoryAbstraction { Abstraction = "personal note", CueAnchors = ["orion enthusiast"] });

        var result = await CreateService(ConfigWith(HarmonicMemoryMode.Full)).RecallAsync("orion");

        result.Should().Contain(n => n.Id == $"{DefaultNs}:orion", "X is a legacy direct-key hit");
        result.Should().Contain(n => n.Id == $"{DefaultNs}:fan", "Y is a harmonic cue-anchor hit");
    }

    [Fact]
    public async Task ScoreTie_LegacyHitWins_NotDroppedByHarmonicOnlyMatch()
    {
        // A legacy-only hit (found by direct key lookup, carries no abstraction) and a harmonic-only hit
        // (matched via its abstraction) tie on fused score. With maxResults=1, the legacy hit — which Off
        // mode would return — must survive; harmonic-only matches never displace a tying legacy result.
        var legacyOnly = new GraphNode
        {
            Id = $"{DefaultNs}:orion",
            Name = "orion",
            Type = "Fact",
            Properties = new Dictionary<string, string> { ["content"] = "the legacy orion fact" }
        };
        await _graphStore.AddNodesAsync([legacyOnly]);
        await SeedMemoryNodeAsync($"{DefaultNs}:fan", "fan", "a fan note",
            new MemoryAbstraction { Abstraction = "orion enthusiast" });

        var result = await CreateService(ConfigWith(HarmonicMemoryMode.Full)).RecallAsync("orion", maxResults: 1);

        result.Should().ContainSingle(n => n.Id == $"{DefaultNs}:orion",
            "on a score tie the legacy path's hit is preserved, not displaced by a harmonic-only match");
    }

    [Fact]
    public async Task QuarantinedNode_NeverSurfaces_EvenOnAbstractionMatch()
    {
        await SeedMemoryNodeAsync($"{DefaultNs}:evil", "evil", "poison payload",
            new MemoryAbstraction { Abstraction = "exfiltration plan", CueAnchors = ["exfiltrate data"] },
            MemoryTrust.Untrusted);

        var result = await CreateService(ConfigWith(HarmonicMemoryMode.Full)).RecallAsync("exfiltrate");

        result.Should().BeEmpty("a quarantined node is never served by recall, even when its abstraction matches");
    }

    [Fact]
    public async Task OtherScopeNode_NotReturned_OnRecall()
    {
        // A node in another tenant/user scope with a matching abstraction must never cross the scope boundary.
        await SeedMemoryNodeAsync("memory:tenant-x:user-y:secret", "secret", "someone else's fact",
            new MemoryAbstraction { Abstraction = "Project Orion timeline" });

        var result = await CreateService(ConfigWith(HarmonicMemoryMode.Full), new FakeScope()).RecallAsync("timeline");

        result.Should().BeEmpty("harmonic recall is restricted to the caller's own scope namespace");
    }

    [Fact]
    public async Task Off_DoesNotUseHarmonicScaffolding()
    {
        // Guards the byte-identical-legacy contract: with Mode Off, a node findable only via its abstraction
        // is not surfaced — recall behaves exactly as it did before harmonic memory existed.
        await SeedMemoryNodeAsync($"{DefaultNs}:pref-1", "pref-1", "The user is vegetarian",
            new MemoryAbstraction { Abstraction = "user dietary preference", CueAnchors = ["vegetarian diet"] });

        var result = await CreateService(ConfigWith(HarmonicMemoryMode.Off)).RecallAsync("dietary");

        result.Should().BeEmpty("Off is the legacy path and never reads the abstraction/cue-anchor index");
    }

    // --- Helpers ---

    private async Task SeedMemoryNodeAsync(
        string id, string name, string content, MemoryAbstraction abstraction,
        MemoryTrust trust = MemoryTrust.Trusted)
    {
        var node = new GraphNode
        {
            Id = id,
            Name = name,
            Type = "Fact",
            Properties = new Dictionary<string, string> { ["content"] = content }
        }.WithAbstraction(abstraction);

        if (trust == MemoryTrust.Untrusted)
            node = node.WithTrust(MemoryTrust.Untrusted);

        await _graphStore.AddNodesAsync([node]);
    }

    private KnowledgeMemoryService CreateService(AppConfig config, IKnowledgeScope? scope = null)
    {
        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(config);

        return new KnowledgeMemoryService(
            new InMemorySessionCache(),
            _graphStore,
            scope ?? new FakeScope(),
            feedbackDetector: null,
            feedbackStore: null,
            monitor.Object,
            NullLogger<KnowledgeMemoryService>.Instance,
            writeGate: null,
            abstractor: null,
            consolidator: null);
    }

    private static AppConfig ConfigWith(HarmonicMemoryMode mode, int fanout = 3, double rrfK = 60.0) => new()
    {
        AI = new AIConfig
        {
            Rag = new RagConfig { GraphRag = new GraphRagConfig { FeedbackAlpha = 0.3 } },
            HarmonicMemory = new HarmonicMemoryConfig
            {
                Mode = mode,
                RecallCueAnchorFanout = fanout,
                RecallRrfK = rrfK
            }
        }
    };

    private sealed class FakeScope : IKnowledgeScope
    {
        public string? UserId { get; set; }
        public string? TenantId { get; set; }
        public string? DatasetId { get; set; }
        public string? DatasetName { get; set; }
        public string? DatasetOwnerId { get; set; }
        public string? AgentId { get; set; }
        public string? ConversationId { get; set; }
    }
}
