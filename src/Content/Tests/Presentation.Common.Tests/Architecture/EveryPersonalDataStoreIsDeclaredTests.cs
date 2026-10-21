using FluentAssertions;
using Tests.Common;
using Xunit;

namespace Presentation.Common.Tests.Architecture;

/// <summary>
/// Architecture guard (#899): every production type that stores data is accounted for in the personal-data
/// inventory — declared a store, declared a channel, or listed in <see cref="HoldsNoPersonalData"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why it reads compiled code.</strong> Erasure was once wired store by store from a hand-kept list, and the
/// conversation transcripts fell outside it because nobody added them. A guard that checked a list of our own store
/// interfaces would repeat that: a store nobody listed is invisible. <see cref="PersistenceScan"/> instead finds every
/// type that calls a persistence primitive (a storage SDK, a file write, the distributed cache), so a new store is
/// caught whether or not anyone thought of it.
/// </para>
/// <para>
/// <strong>What becomes required, transitively:</strong>
/// </para>
/// <list type="number">
/// <item><description>Every type that calls a persistence primitive.</description></item>
/// <item><description>
/// Every type that calls a <c>[PersonalDataChannel]</c>: the shared audit writer and the database contexts write for
/// the types that call them, so those callers are where the data is described.
/// </description></item>
/// <item><description>
/// Every other production class that shares a base type or interface with a <c>[PersonalDataStore]</c>: an in-memory
/// graph store calls no primitive, but it holds the same data as the durable one beside it. Interfaces are skipped;
/// they hold nothing.
/// </description></item>
/// </list>
/// <para>
/// <strong>An exemption is pinned to what the scan saw.</strong> Each entry in <see cref="HoldsNoPersonalData"/> records
/// every reason the type is required. When that changes — a file write added to an excused registration class — the
/// entry fails until someone re-reads the type and updates it, so an exemption never silently excuses new storage.
/// </para>
/// <para>
/// <strong>Blind spots, stated rather than hidden:</strong> a type that writes only through an interface whose
/// implementation is a channel is not reached through that call; persistence over HTTP to a remote service, through
/// reflection, <c>dynamic</c>, configuration-only sinks or native code is not seen. The sibling rule above reaches the
/// most common case (memory-only stores beside a durable one); the learnings and cross-session memory stores, which
/// write through graph interfaces, are declared by hand for the same reason.
/// </para>
/// </remarks>
public sealed class EveryPersonalDataStoreIsDeclaredTests
{
    private const string StoreAttribute = "Domain.Common.Compliance.PersonalDataStoreAttribute";
    private const string ChannelAttribute = "Domain.Common.Compliance.PersonalDataChannelAttribute";

    /// <summary>
    /// One scan per test run. Each scan reads every production assembly; repeating it per test is the kind of
    /// redundant I/O <c>SourceScan</c>'s cache notes recorded destabilising the timing-sensitive sandbox tests.
    /// </summary>
    private static readonly Lazy<Inventory> Scanned = new(Scan);

    [Fact]
    public void EveryTypeThatStoresData_IsAccountedFor()
    {
        var inventory = Scanned.Value;

        var unaccounted = inventory.Required
            .Where(r => !IsDeclared(inventory.Facts[r.Key]) && !HoldsNoPersonalData.Entries.ContainsKey(r.Key))
            .Select(r => $"  {r.Key}  ({string.Join("; ", r.Value)})")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        unaccounted.Should().BeEmpty(
            "every type that stores data must carry [PersonalDataStore] (it holds personal data), [PersonalDataChannel] " +
            "(it writes for its callers) or be listed in HoldsNoPersonalData with a reason. Unaccounted:" +
            Environment.NewLine + string.Join(Environment.NewLine, unaccounted));
    }

    [Fact]
    public void EveryExemption_StillMatchesWhatTheScanSees()
    {
        var inventory = Scanned.Value;

        var stale = HoldsNoPersonalData.Entries
            .Where(e => !inventory.Required.TryGetValue(e.Key, out var seen) || !seen.SetEquals(e.Value.Evidence))
            .Select(e => Suggest(e.Key, e.Value.Reason, inventory.Required.GetValueOrDefault(e.Key)))
            .ToList();

        stale.Should().BeEmpty(
            "an exemption excuses the storage the scan saw when it was written. When that changes, re-read the type: if " +
            "it now holds personal data, declare it; otherwise update the entry. Current scan for each changed entry:" +
            Environment.NewLine + string.Join(Environment.NewLine, stale));
    }

    [Fact]
    public void NoExemptType_IsAlsoDeclared()
    {
        var inventory = Scanned.Value;

        HoldsNoPersonalData.Entries.Keys
            .Where(k => inventory.Facts.TryGetValue(k, out var type) && IsDeclared(type))
            .Should().BeEmpty("a type is either described in the inventory or excused from it, not both");
    }

    [Fact]
    public void NoTypeIsBothAStoreAndAChannel()
    {
        Scanned.Value.Facts.Values
            .Where(f => f.Attributes.ContainsKey(StoreAttribute) && f.Attributes.ContainsKey(ChannelAttribute))
            .Select(f => f.Name)
            .Should().BeEmpty("a type either describes its data or passes data through for callers that do");
    }

    /// <summary>
    /// <c>ErasureSupport.PurgeByOwner</c>'s value. Enums are decoded from metadata as their underlying integer, and the
    /// descriptor's erasure support is its fifth constructor argument.
    /// </summary>
    private const int PurgeByOwner = 0;
    private const int ErasureArgument = 4;
    private const string EraserInterface = "Application.AI.Common.Interfaces.Compliance.IPersonalDataEraser";

    /// <summary>
    /// Stores that say they purge by owner without implementing <c>IPersonalDataEraser</c> themselves, because the
    /// knowledge-graph erasure (<c>DefaultErasureOrchestrator</c>) reaches them, with how it does.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ErasedThroughKnowledgeGraph = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Infrastructure.AI.KnowledgeGraph.Neo4j.Neo4jGraphStore"] = "the orchestrator deletes the owner's nodes and edges through IKnowledgeGraphStore",
        ["Infrastructure.AI.KnowledgeGraph.PostgreSql.PostgreSqlGraphStore"] = "the orchestrator deletes the owner's nodes and edges through IKnowledgeGraphStore",
        ["Infrastructure.AI.KnowledgeGraph.InMemory.InMemoryGraphStore"] = "the orchestrator deletes the owner's nodes and edges through IKnowledgeGraphStore",
        ["Infrastructure.AI.KnowledgeGraph.Learnings.GraphLearningsStore"] = "learnings are owner-stamped graph nodes, deleted with the owner's nodes",
        ["Infrastructure.AI.RAG.GraphRag.CrossSessionMemoryStore"] = "the orchestrator calls ICrossSessionMemoryStore.PurgeByOwnerAsync",
        ["Infrastructure.AI.RAG.GraphRag.KuzuGraphBackend"] = "cross-session memory's purge deletes the owner's memory nodes from this backend",
    };

    [Fact]
    public void ADescriptorSaysPurgeByOwner_ExactlyWhenTheStoreCanBeErased()
    {
        var stores = Scanned.Value.Facts.Values.Where(f => f.Attributes.ContainsKey(StoreAttribute)).ToList();

        static bool SaysPurge(PersistenceScan.TypeFacts store) =>
            store.Attributes[StoreAttribute] is { Count: > ErasureArgument } args && args[ErasureArgument] is PurgeByOwner;

        var erasersNotSayingSo = stores
            .Where(s => s.Supertypes.Contains(EraserInterface) && !SaysPurge(s))
            .Select(s => s.Name);
        var sayingSoWithoutEraser = stores
            .Where(s => SaysPurge(s) && !s.Supertypes.Contains(EraserInterface) && !ErasedThroughKnowledgeGraph.ContainsKey(s.Name))
            .Select(s => s.Name);

        erasersNotSayingSo.Concat(sayingSoWithoutEraser).Should().BeEmpty(
            "a descriptor that says PurgeByOwner is what erasure, retention and export work plan against; it must be " +
            "true. A store implementing IPersonalDataEraser must say so, and one saying so must implement it or be " +
            "reached through the knowledge-graph erasure (listed in ErasedThroughKnowledgeGraph)");
    }

    [Fact]
    public void EveryStoreErasedThroughTheGraph_IsStillOne()
    {
        var byName = Scanned.Value.Facts.Values.Where(f => f.Attributes.ContainsKey(StoreAttribute)).ToDictionary(f => f.Name);

        ErasedThroughKnowledgeGraph.Keys
            .Where(name => !byName.TryGetValue(name, out var store)
                || store.Supertypes.Contains(EraserInterface)
                || store.Attributes[StoreAttribute] is not { Count: > ErasureArgument } args
                || args[ErasureArgument] is not PurgeByOwner)
            .Should().BeEmpty("an entry must name a declared store that says PurgeByOwner and does not erase itself");
    }

    [Fact]
    public void EveryDescriptor_SaysWhatItHolds()
    {
        Scanned.Value.Facts.Values
            .Where(f => f.Attributes.TryGetValue(StoreAttribute, out var args) && args is not [string { Length: > 20 }, ..])
            .Select(f => f.Name)
            .Should().BeEmpty("a descriptor is read by a privacy reviewer; its description must say what is kept and why");
    }

    private sealed record Inventory(
        IReadOnlyDictionary<string, PersistenceScan.TypeFacts> Facts,
        IReadOnlyDictionary<string, SortedSet<string>> Required);

    private static Inventory Scan()
    {
        var facts = PersistenceScan.Scan(PersistenceScan.ProductionAssemblyPaths(Path.Combine(RepoRoot.Path, "src", "Content")));

        // A vacuous guard reads as protection; the floor is near the production tree's real size.
        facts.Should().HaveCountGreaterThan(2000, "the scan must see the production assemblies, not a sliver of them");

        return new Inventory(facts, RequiredTypes(facts));
    }

    /// <summary>Every type the inventory must account for, by scan key, with every reason it is required.</summary>
    private static IReadOnlyDictionary<string, SortedSet<string>> RequiredTypes(IReadOnlyDictionary<string, PersistenceScan.TypeFacts> facts)
    {
        var callers = facts
            .SelectMany(f => f.Value.Callees.Select(callee => (Callee: callee, Caller: f.Key)))
            .ToLookup(x => x.Callee, x => x.Caller);
        var bySupertype = facts
            .SelectMany(f => f.Value.Supertypes.Select(s => (Supertype: s, Type: f.Key)))
            .ToLookup(x => x.Supertype, x => x.Type);
        var productionNames = facts.Values.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);

        var required = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();

        void Visit(string key)
        {
            if (visited.Add(key))
                queue.Enqueue(key);
        }

        void Require(string key, string reason)
        {
            if (!required.TryGetValue(key, out var reasons))
            {
                reasons = new SortedSet<string>(StringComparer.Ordinal);
                required[key] = reasons;
            }

            reasons.Add(reason);
            Visit(key);
        }

        foreach (var (key, type) in facts.Where(f => f.Value.Primitives.Count > 0))
            foreach (var primitive in type.Primitives)
                Require(key, primitive);

        // A store declared by hand because it writes through an interface the scan cannot follow still has siblings,
        // so every declared type is a starting point, not only the ones the scan found.
        foreach (var key in facts.Where(f => IsDeclared(f.Value)).Select(f => f.Key))
            Visit(key);

        while (queue.TryDequeue(out var key))
        {
            var type = facts[key];
            if (type.Attributes.ContainsKey(ChannelAttribute))
            {
                foreach (var caller in callers[key])
                    Require(caller, "calls channel " + type.Name);
            }

            if (type.Attributes.ContainsKey(StoreAttribute))
            {
                // The erasure contract is shared by every erasable store whatever it holds; it is not a storage seam.
                foreach (var supertype in type.Supertypes.Where(s => productionNames.Contains(s) && s != EraserInterface))
                    foreach (var sibling in bySupertype[supertype].Where(s => s != key && !facts[s].IsInterface))
                        Require(sibling, $"shares {supertype} with store {type.Name}");
            }
        }

        return required;
    }

    private static string Suggest(string key, string reason, SortedSet<string>? seen) =>
        seen is null
            ? $"  {key}: no longer required by the scan; remove the entry"
            : $"  [\"{key}\"] = new(\"{reason}\", {string.Join(", ", seen.Select(s => "\"" + s + "\""))}),";

    private static bool IsDeclared(PersistenceScan.TypeFacts type) =>
        type.Attributes.ContainsKey(StoreAttribute) || type.Attributes.ContainsKey(ChannelAttribute);
}
