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
/// write through graph interfaces, are declared by hand for the same reason. A memory-only store with no declared
/// sibling is held in the inventory in two ways: every type implementing <c>IPersonalDataEraser</c> or
/// <c>IPersonalDataExporter</c> must carry the descriptor (#926, the run store), and every type keeping a collection of
/// records that name a person must too, whatever it calls (#990, the bundle run store; the rule and its own blind spots
/// are in <c>EveryPersonalDataStoreIsDeclaredTests.InMemory.cs</c>).
/// </para>
/// </remarks>
public sealed partial class EveryPersonalDataStoreIsDeclaredTests
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
    /// Stores that say they purge by owner without implementing <c>IPersonalDataEraser</c> themselves, because another
    /// store's erasure reaches them (the knowledge-graph erasure, <c>DefaultErasureOrchestrator</c>, or the run store's),
    /// with how it does.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ErasedThroughAnotherStore = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Infrastructure.AI.KnowledgeGraph.Neo4j.Neo4jGraphStore"] = "the orchestrator deletes the owner's nodes and edges through IKnowledgeGraphStore",
        ["Infrastructure.AI.KnowledgeGraph.PostgreSql.PostgreSqlGraphStore"] = "the orchestrator deletes the owner's nodes and edges through IKnowledgeGraphStore",
        ["Infrastructure.AI.KnowledgeGraph.InMemory.InMemoryGraphStore"] = "the orchestrator deletes the owner's nodes and edges through IKnowledgeGraphStore",
        ["Infrastructure.AI.KnowledgeGraph.Learnings.GraphLearningsStore"] = "learnings are owner-stamped graph nodes, deleted with the owner's nodes",
        ["Infrastructure.AI.RAG.GraphRag.CrossSessionMemoryStore"] = "the orchestrator calls ICrossSessionMemoryStore.PurgeByOwnerAsync",
        ["Infrastructure.AI.RAG.GraphRag.KuzuGraphBackend"] = "cross-session memory's purge deletes the owner's memory nodes from this backend",
        ["Infrastructure.AI.Runs.InMemoryEvalRunSubmissionStore"] = "the run store's erasure removes the person's finished runs and tells EvalSubmissionReclaimListener, which releases their submissions",
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
            .Where(s => SaysPurge(s) && !s.Supertypes.Contains(EraserInterface) && !ErasedThroughAnotherStore.ContainsKey(s.Name))
            .Select(s => s.Name);

        erasersNotSayingSo.Concat(sayingSoWithoutEraser).Should().BeEmpty(
            "a descriptor that says PurgeByOwner is what erasure, retention and export work plan against; it must be " +
            "true. A store implementing IPersonalDataEraser must say so, and one saying so must implement it or be " +
            "reached through another store's erasure (listed in ErasedThroughAnotherStore)");
    }

    /// <summary>
    /// Types that erase or export for a person without being a store of their own, with why. Each is checked to still be
    /// one, so the list cannot excuse a store that later starts keeping data.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ErasesOrExportsForOthers = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Application.AI.Common.Services.Compliance.NonErasableStore"] =
            "the stand-in AddPersonalDataEraser puts on the receipt for a declared store that cannot erase; it holds nothing",
        ["Infrastructure.AI.KnowledgeGraph.Compliance.KnowledgeGraphExporter"] =
            "reads the owner's nodes and edges through IKnowledgeGraphStore; the graph stores it reads are declared",
        ["Infrastructure.AI.Runs.EvalRunSubmissionExporter"] =
            "reads the submissions of the person's runs through IEvalRunSubmissionStore, by the ids IRunJobStore lists; both stores are declared",
    };

    [Fact]
    public void EveryTypeThatErasesOrExports_IsADeclaredStore()
    {
        // The scan finds a store by the persistence it calls, so a store held only in memory with no declared sibling is
        // invisible to it (#926: the run store). One that erases or exports a person's data holds it, whatever it calls, so
        // its descriptor is what keeps it in the inventory; removing the descriptor must fail here, not pass silently.
        Scanned.Value.Facts.Values
            .Where(f => !f.IsInterface && !IsDeclared(f) && !ErasesOrExportsForOthers.ContainsKey(f.Name)
                && (f.Supertypes.Contains(EraserInterface) || f.Supertypes.Contains(ExporterInterface)))
            .Select(f => f.Name)
            .Order(StringComparer.Ordinal)
            .Should().BeEmpty(
                "a type implementing IPersonalDataEraser or IPersonalDataExporter holds a person's data and must carry " +
                "[PersonalDataStore], or be listed in ErasesOrExportsForOthers with what it erases or exports for");
    }

    [Fact]
    public void EveryTypeErasingOrExportingForOthers_IsStillOne()
    {
        var byName = Scanned.Value.Facts.Values.Where(f => !f.IsInterface).ToLookup(f => f.Name, StringComparer.Ordinal);

        ErasesOrExportsForOthers.Keys
            .Where(name => !byName[name].Any(type => !IsDeclared(type)
                && (type.Supertypes.Contains(EraserInterface) || type.Supertypes.Contains(ExporterInterface))))
            .Should().BeEmpty("an entry must name an undeclared type that still erases or exports");
    }

    [Fact]
    public void EveryStoreErasedThroughAnotherStore_IsStillOne()
    {
        var byName = Scanned.Value.Facts.Values.Where(f => f.Attributes.ContainsKey(StoreAttribute)).ToDictionary(f => f.Name);

        ErasedThroughAnotherStore.Keys
            .Where(name => !byName.TryGetValue(name, out var store)
                || store.Supertypes.Contains(EraserInterface)
                || store.Attributes[StoreAttribute] is not { Count: > ErasureArgument } args
                || args[ErasureArgument] is not PurgeByOwner)
            .Should().BeEmpty("an entry must name a declared store that says PurgeByOwner and does not erase itself");
    }

    /// <summary>
    /// <c>ExportSupport.ByOwner</c>'s value, and the descriptor's export support is its sixth constructor argument.
    /// </summary>
    private const int ExportByOwner = 0;
    private const int ExportArgument = 5;
    private const string ExporterInterface = "Application.AI.Common.Interfaces.Compliance.IPersonalDataExporter";

    /// <summary>
    /// Stores that say they export by owner without implementing <c>IPersonalDataExporter</c> themselves, because another
    /// store's exporter reads exactly what they hold of a person (#902), with which one and how.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ExportedThroughAnotherStore = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Infrastructure.AI.KnowledgeGraph.Neo4j.Neo4jGraphStore"] = "KnowledgeGraphExporter reads the owner's nodes and edges through IKnowledgeGraphStore",
        ["Infrastructure.AI.KnowledgeGraph.PostgreSql.PostgreSqlGraphStore"] = "KnowledgeGraphExporter reads the owner's nodes and edges through IKnowledgeGraphStore",
        ["Infrastructure.AI.KnowledgeGraph.InMemory.InMemoryGraphStore"] = "KnowledgeGraphExporter reads the owner's nodes and edges through IKnowledgeGraphStore",
        ["Infrastructure.AI.KnowledgeGraph.Learnings.GraphLearningsStore"] = "learnings are owner-stamped graph nodes, which KnowledgeGraphExporter reads with the owner's other nodes",
        ["Infrastructure.AI.RAG.GraphRag.KuzuGraphBackend"] = "its only owner-stamped rows are cross-session memory nodes, which CrossSessionMemoryStore exports from this backend",
        ["Infrastructure.AI.Runs.InMemoryEvalRunSubmissionStore"] = "EvalRunSubmissionExporter reads the submissions of the person's runs, found through the run store's records",
    };

    /// <summary>
    /// Stores that can find and erase one person's data but deliberately do not return it in a copy, with why. Empty:
    /// every store that purges by owner exports. An entry needs a reason a privacy team would accept.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> PurgedButNotExported = new Dictionary<string, string>(StringComparer.Ordinal);

    [Fact]
    public void EveryStoreThatPurgesByOwner_AlsoExportsByOwner()
    {
        // A store that can find one person's data to erase it can find it to copy it; saying it cannot leaves a gap in the
        // person's copy that the manifest has to admit to on every request (#902).
        var stores = Scanned.Value.Facts.Values.Where(f => f.Attributes.ContainsKey(StoreAttribute)).ToList();

        stores
            .Where(s => s.Attributes[StoreAttribute] is { Count: > ExportArgument } args && args[ErasureArgument] is PurgeByOwner)
            .Where(s => !SaysExportByOwner(s) && !SaysExportNotApplicable(s) && !PurgedButNotExported.ContainsKey(s.Name))
            .Select(s => s.Name)
            .Should().BeEmpty(
                "a store whose descriptor says PurgeByOwner must also say ExportSupport.ByOwner, or NotApplicable when it " +
                "holds nothing a person would receive (its description gives the reason), or be listed in " +
                "PurgedButNotExported with the reason it is left out of the person's copy");
    }

    /// <summary><c>ExportSupport.NotApplicable</c>'s value: a declared decision the store holds nothing a person would receive.</summary>
    private const int ExportNotApplicable = 2;

    private static bool SaysExportNotApplicable(PersistenceScan.TypeFacts store) =>
        store.Attributes[StoreAttribute] is { Count: > ExportArgument } args && args[ExportArgument] is ExportNotApplicable;

    [Fact]
    public void EveryStorePurgedButNotExported_IsStillOne()
    {
        var byName = Scanned.Value.Facts.Values.Where(f => f.Attributes.ContainsKey(StoreAttribute)).ToDictionary(f => f.Name);

        PurgedButNotExported.Keys
            .Where(name => !byName.TryGetValue(name, out var store)
                || store.Attributes[StoreAttribute] is not { Count: > ExportArgument } args
                || args[ErasureArgument] is not PurgeByOwner
                || SaysExportByOwner(store))
            .Should().BeEmpty("an entry must name a declared store that purges by owner and does not export");
    }

    private static bool SaysExportByOwner(PersistenceScan.TypeFacts store) =>
        store.Attributes[StoreAttribute] is { Count: > ExportArgument } args && args[ExportArgument] is ExportByOwner;

    [Fact]
    public void ADescriptorSaysExportByOwner_ExactlyWhenTheStoreCanExport()
    {
        var stores = Scanned.Value.Facts.Values.Where(f => f.Attributes.ContainsKey(StoreAttribute)).ToList();

        var exportersNotSayingSo = stores
            .Where(s => s.Supertypes.Contains(ExporterInterface) && !SaysExportByOwner(s))
            .Select(s => s.Name);
        var sayingSoWithoutExporter = stores
            .Where(s => SaysExportByOwner(s) && !s.Supertypes.Contains(ExporterInterface) && !ExportedThroughAnotherStore.ContainsKey(s.Name))
            .Select(s => s.Name);

        exportersNotSayingSo.Concat(sayingSoWithoutExporter).Should().BeEmpty(
            "a descriptor that says ExportSupport.ByOwner tells a privacy team the person's copy includes this store; it " +
            "must be true. A store implementing IPersonalDataExporter must say so, and one saying so must implement it or " +
            "be read through another store's export (listed in ExportedThroughAnotherStore)");
    }

    [Fact]
    public void EveryStoreExportedThroughTheGraph_IsStillOne()
    {
        var byName = Scanned.Value.Facts.Values.Where(f => f.Attributes.ContainsKey(StoreAttribute)).ToDictionary(f => f.Name);

        ExportedThroughAnotherStore.Keys
            .Where(name => !byName.TryGetValue(name, out var store)
                || store.Supertypes.Contains(ExporterInterface)
                || !SaysExportByOwner(store))
            .Should().BeEmpty("an entry must name a declared store that says ExportSupport.ByOwner and does not export itself");
    }

    /// <summary>
    /// <c>RetentionBasis.Unbounded</c>'s value, and the descriptor's retention basis is its fourth constructor argument.
    /// </summary>
    private const int Unbounded = 3;
    private const int RetentionArgument = 3;

    private static IEnumerable<string> StoresKeptForever() =>
        Scanned.Value.Facts.Values
            .Where(f => f.Attributes.TryGetValue(StoreAttribute, out var args)
                && args is { Count: > RetentionArgument } && args[RetentionArgument] is Unbounded)
            .Select(f => f.Name);

    [Fact]
    public void NoStoreKeepsDataForever_OutsideTheKnownGaps()
    {
        StoresKeptForever()
            .Where(name => !KeptForeverGaps.Entries.ContainsKey(name))
            .Order(StringComparer.Ordinal)
            .Should().BeEmpty(
                "storage limitation (#901): a store needs a retention period enforced by something that deletes expired " +
                "records (RetentionBasis.Configured), or a written reason to keep it (KeptByDesign). Unbounded is a gap, " +
                "and a new one is not allowed");
    }

    [Fact]
    public void EveryKnownGap_IsStillOne()
    {
        var keptForever = StoresKeptForever().ToHashSet(StringComparer.Ordinal);

        KeptForeverGaps.Entries.Keys
            .Where(name => !keptForever.Contains(name))
            .Order(StringComparer.Ordinal)
            .Should().BeEmpty("a gap that has been closed must leave KeptForeverGaps, so the list only ever shrinks");
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
                // The erasure and export contracts are shared by every store that supports them whatever it holds; they
                // are not storage seams, and an exporter that reads another store (KnowledgeGraphExporter) holds nothing.
                foreach (var supertype in type.Supertypes.Where(s => productionNames.Contains(s) && s is not (EraserInterface or ExporterInterface)))
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
