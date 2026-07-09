using Application.AI.Common.Interfaces.KnowledgeGraph;
using Domain.AI.KnowledgeGraph.Models;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.RAG;
using FluentAssertions;
using Infrastructure.AI.KnowledgeGraph.InMemory;
using Infrastructure.AI.KnowledgeGraph.Scoping;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.KnowledgeGraph.Tests.Scoping;

/// <summary>
/// Regression tests for the owner/tenant identity canonicalization fix (D3, Lane 2).
/// </summary>
/// <remarks>
/// The authorization gate (<see cref="KnowledgeScopeValidator"/>) compared owner/tenant IDs
/// case-insensitively while every storage backend filtered case-sensitively. That drift let the
/// gate authorize a right-to-erasure the store then failed to match — silently leaving the
/// subject's data in place. These tests pin the closed behavior: identities are canonicalized on
/// write and before the exact filter, so authorization and persistence agree.
/// </remarks>
public sealed class OwnerIdCanonicalizationTests
{
    /// <summary>
    /// The core drift, end-to-end: an owner id differing only in case is authorized by the gate,
    /// and the store's owner filter must then actually find the subject's node. Pre-fix the store
    /// filtered case-sensitively and returned nothing — the data survived the "authorized" erasure.
    /// </summary>
    [Fact]
    public async Task GateAuthorizesMixedCaseOwner_And_StoreOwnerFilterMatchesIt()
    {
        // A fact was remembered under a mixed-case owner id.
        const string storedOwner = "Alice@Example.com";
        // The erasure request arrives with the same principal in a different case.
        const string erasureRequestOwner = "alice@example.COM";

        var validator = CreateValidator(isolationEnabled: true);
        var scope = CreateScope(userId: erasureRequestOwner);

        // Gate: same principal → authorized.
        validator.CanAccessDataset(scope, erasureRequestOwner).Should().BeTrue();

        // Store: the authorized owner id must actually match the subject's node.
        var store = new InMemoryGraphStore(Mock.Of<ILogger<InMemoryGraphStore>>());
        await store.AddNodesAsync([new GraphNode
        {
            Id = "n1", Name = "secret", Type = "Fact", OwnerId = storedOwner
        }]);

        var owned = await store.GetNodesByOwnerAsync(erasureRequestOwner);

        owned.Should().ContainSingle("the store must find the subject's data the gate authorized erasing")
            .Which.Id.Should().Be("n1");
    }

    /// <summary>
    /// The owner-edge erasure sweep must delete edges whose owner differs only in case from the
    /// requested (authorized) owner id. Pre-fix the case-sensitive filter deleted nothing.
    /// </summary>
    [Fact]
    public async Task DeleteEdgesByOwner_MixedCaseOwner_ErasesTheSubjectsEdge()
    {
        var store = new InMemoryGraphStore(Mock.Of<ILogger<InMemoryGraphStore>>());
        await store.AddNodesAsync([
            new GraphNode { Id = "n1", Name = "A", Type = "Fact" },
            new GraphNode { Id = "n2", Name = "B", Type = "Fact" }
        ]);
        await store.AddEdgesAsync([new GraphEdge
        {
            Id = "e1", SourceNodeId = "n1", TargetNodeId = "n2",
            Predicate = "relates_to", ChunkId = "c1", OwnerId = "Alice@Example.com"
        }]);

        var deleted = await store.DeleteEdgesByOwnerAsync("ALICE@EXAMPLE.COM");

        deleted.Should().BeEquivalentTo(["e1"]);
        (await store.GetEdgeCountAsync()).Should().Be(0, "the subject's edge must be erased");
    }

    /// <summary>
    /// Owner ids are canonical in the store regardless of the casing supplied on write, so a
    /// canonically-cased query matches a mixed-case write.
    /// </summary>
    [Fact]
    public async Task Write_MixedCaseOwner_IsStoredCanonically()
    {
        var store = new InMemoryGraphStore(Mock.Of<ILogger<InMemoryGraphStore>>());
        await store.AddNodesAsync([new GraphNode
        {
            Id = "n1", Name = "secret", Type = "Fact", OwnerId = "  Alice@Example.com  "
        }]);

        var stored = await store.GetNodeAsync("n1");

        stored!.OwnerId.Should().Be("alice@example.com", "owner is canonicalized (trimmed, lower-cased) on write");
    }

    /// <summary>
    /// Gate guard: two owner ids differing only in case denote the same principal.
    /// </summary>
    [Fact]
    public void CanAccessDataset_MixedCaseOwner_TreatedAsSamePrincipal()
    {
        var validator = CreateValidator(isolationEnabled: true);
        var scope = CreateScope(userId: "alice@example.com");

        validator.CanAccessDataset(scope, "Alice@Example.COM").Should().BeTrue();
    }

    // --- Absent-owner guard: a null/empty/whitespace owner canonicalizes to null, which denotes an
    // absent/global owner and must never over-match the owner-null (shared) corpus or authorize
    // access. Without these guards, string.Equals(record.OwnerId, null) is true for every shared
    // record, so an empty/whitespace erase request would mass-delete the shared graph on the
    // in-memory backend (the DEFAULT: managed_code aliases to in_memory). ---

    /// <summary>
    /// A by-owner NODE query with an absent (empty/whitespace/null) owner must return nothing — and
    /// crucially must NOT return the owner-null (shared/global) records. Pins the fix and the
    /// cross-backend agreement (Neo4j/Postgres already no-match on null via SQL null semantics).
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task GetNodesByOwner_AbsentOwner_ReturnsEmpty_AndDoesNotTouchGlobalRecords(string? absentOwner)
    {
        var store = new InMemoryGraphStore(Mock.Of<ILogger<InMemoryGraphStore>>());
        await store.AddNodesAsync([
            new GraphNode { Id = "shared1", Name = "Corpus", Type = "Fact" },   // owner-null = shared/global
            new GraphNode { Id = "shared2", Name = "Learning", Type = "Fact" }, // owner-null = shared/global
            new GraphNode { Id = "owned", Name = "Memory", Type = "Fact", OwnerId = "alice@example.com" }
        ]);

        var result = await store.GetNodesByOwnerAsync(absentOwner!);

        result.Should().BeEmpty("an absent owner is never a queryable subject and must not match shared records");
    }

    /// <summary>
    /// A by-owner EDGE erasure with an absent owner must be a no-op — and must NOT delete the
    /// owner-null (shared) edges. This is the data-destruction guard: on the default in-memory
    /// backend, an empty erase request must not wipe the shared graph.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task DeleteEdgesByOwner_AbsentOwner_IsNoOp_AndPreservesGlobalEdges(string? absentOwner)
    {
        var store = new InMemoryGraphStore(Mock.Of<ILogger<InMemoryGraphStore>>());
        await store.AddNodesAsync([
            new GraphNode { Id = "n1", Name = "A", Type = "Fact" },
            new GraphNode { Id = "n2", Name = "B", Type = "Fact" }
        ]);
        await store.AddEdgesAsync([
            new GraphEdge { Id = "e-shared1", SourceNodeId = "n1", TargetNodeId = "n2", Predicate = "cites", ChunkId = "c1" },  // owner-null
            new GraphEdge { Id = "e-shared2", SourceNodeId = "n2", TargetNodeId = "n1", Predicate = "cites", ChunkId = "c1" },  // owner-null
            new GraphEdge { Id = "e-owned", SourceNodeId = "n1", TargetNodeId = "n2", Predicate = "uses", ChunkId = "c1", OwnerId = "alice@example.com" }
        ]);

        var deleted = await store.DeleteEdgesByOwnerAsync(absentOwner!);

        deleted.Should().BeEmpty("an absent owner erases nothing");
        (await store.GetEdgeCountAsync()).Should().Be(3, "the shared (owner-null) corpus must survive an absent-owner erase");
    }

    /// <summary>
    /// Gate guard (LOW): an absent dataset owner is never an authorizable dataset, even for a caller
    /// whose own UserId is also absent — <see cref="ScopeIdentity.AreSame"/> would otherwise treat two
    /// absent ids as equal and authorize access the old OrdinalIgnoreCase gate denied.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void CanAccessDataset_AbsentOwner_Denies(string? absentOwner)
    {
        var validator = CreateValidator(isolationEnabled: true);
        var scope = CreateScope(userId: null);

        validator.CanAccessDataset(scope, absentOwner!).Should().BeFalse();
    }

    private static KnowledgeScopeValidator CreateValidator(bool isolationEnabled)
    {
        var monitor = new Mock<IOptionsMonitor<AppConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(new AppConfig
        {
            AI = new AIConfig
            {
                Rag = new RagConfig
                {
                    GraphRag = new GraphRagConfig { MultiTenantIsolation = isolationEnabled }
                }
            }
        });
        return new KnowledgeScopeValidator(monitor.Object);
    }

    private static IKnowledgeScope CreateScope(string? userId = null, string? tenantId = null)
    {
        var mock = new Mock<IKnowledgeScope>();
        mock.Setup(s => s.UserId).Returns(userId);
        mock.Setup(s => s.TenantId).Returns(tenantId);
        return mock.Object;
    }
}
