using FluentAssertions;
using Tests.Common;
using Xunit;

namespace Presentation.Common.Tests.Architecture;

/// <summary>
/// The in-memory half of the inventory guard (#990): a type that keeps a collection of records naming a person is a store,
/// whatever it calls.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The blind spot it closes.</strong> The scan finds a store by the persistence primitive it calls, and a store
/// held in process memory calls none. Two such stores were missed that way: the run store (#926), found only once it
/// erased, and the bundle run store (#990), which kept conversation content for half an hour, declared nowhere. This rule
/// reads what a type keeps rather than what it calls.
/// </para>
/// <para>
/// <strong>The rule, kept narrow so that what it flags is a store.</strong> A type is a holder when one of its own fields,
/// or a field of a type nested in it, is a collection (or array) whose element names a person: a type with a field
/// <c>OwnerId</c> or <c>UserId</c>, directly or through the types it holds. Left out, because none of them keeps many
/// records past one call: records (a value, not a store), a settable auto-property's collection (a data shape's member,
/// such as an entity's navigation collection), compiler-generated closures and state machines, and a single record held
/// in a field rather than a collection of them. A holder must be
/// declared a store or channel, or listed in <see cref="HoldsOwnerRecordsWithoutStoringThem"/> with a reason, pinned to
/// the fields the scan saw.
/// </para>
/// <para>
/// <strong>Blind spots, stated:</strong> a collection keyed only by a conversation, run or job id, with no owner on its
/// records (the evaluation submissions, the token budgets), is not seen; such a store is declared by hand. Nor is a
/// collection of strings that happen to be owner ids, data held as <c>object</c> (a memory cache), or an owner field under
/// another name. Widening the names would flag tenant- and caller-scoped governance state that holds no personal data;
/// measure the result before doing so.
/// </para>
/// </remarks>
public sealed partial class EveryPersonalDataStoreIsDeclaredTests
{
    /// <summary>The field names that mean a record belongs to a person, the two this codebase stamps records with.</summary>
    private static readonly HashSet<string> PersonFields = new(StringComparer.OrdinalIgnoreCase) { "OwnerId", "UserId" };

    /// <summary>The namespaces whose generic types hold many values: a field of one of them is a collection.</summary>
    private static readonly string[] CollectionNamespaces =
    [
        "System.Collections.Generic.", "System.Collections.Concurrent.", "System.Collections.Immutable.",
        "System.Collections.ObjectModel.",
    ];

    private static readonly Lazy<IReadOnlyDictionary<string, SortedSet<string>>> Holders = new(FindHolders);

    [Fact]
    public void EveryTypeHoldingRecordsThatNameAPerson_IsAccountedFor()
    {
        var unaccounted = Holders.Value
            .Where(h => !IsDeclared(Scanned.Value.Facts[h.Key]) && !HoldsOwnerRecordsWithoutStoringThem.Entries.ContainsKey(h.Key))
            .Select(h => $"  {h.Key}  ({string.Join("; ", h.Value)})")
            .Order(StringComparer.Ordinal)
            .ToList();

        unaccounted.Should().BeEmpty(
            "a type that keeps a collection of records naming a person holds personal data in memory, which no persistence " +
            "call shows: declare it [PersonalDataStore], or list it in HoldsOwnerRecordsWithoutStoringThem with a reason. " +
            "Unaccounted:" + Environment.NewLine + string.Join(Environment.NewLine, unaccounted));
    }

    [Fact]
    public void TheHolderRule_FindsTheInMemoryStoresItWasWrittenFor()
    {
        // A floor, so a rule that stopped matching anything (a renamed field, a broken signature decoder) cannot pass by
        // finding nothing. These are declared stores that call no persistence primitive.
        Holders.Value.Keys.Should().Contain(
        [
            "Infrastructure.AI/Infrastructure.AI.Runs.InMemoryRunJobStore",
            "Infrastructure.AI/Infrastructure.AI.Bundles.InMemoryBundleRunJobStore",
        ]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TypesThatNameAPerson_AChildReachingAPersonOnlyThroughItsParent_IsFoundWhicheverFieldComesFirst(bool childrenFirst)
    {
        // A parent with an owner and a list of children, each child pointing back at its parent: the child names a person
        // only through a type still being decided when the child is first reached.
        static PersistenceScan.TypeShape Shape(string name, params PersistenceScan.TypeShape[] args) => new("A", name, args, false);
        var children = new PersistenceScan.FieldShape("Children", true, Shape("System.Collections.Generic.List`1", Shape("Ns.Child")));
        var owner = new PersistenceScan.FieldShape("OwnerId", true, new("", "System.String", [], false));
        var parent = new PersistenceScan.DeclaredType("Ns.Parent", true, false, childrenFirst ? [children, owner] : [owner, children]);
        var child = new PersistenceScan.DeclaredType("Ns.Child", true, false, [new("Parent", true, Shape("Ns.Parent"))]);

        var found = TypesThatNameAPerson([("A/Ns.Child", child), ("A/Ns.Parent", parent)]);

        found.Should().ContainKey("A/Ns.Child").WhoseValue.Should().Be("Ns.Parent.OwnerId");
    }

    [Theory]
    [InlineData(false, false, true)] // a private field: what every shipped in-memory store uses
    [InlineData(true, true, true)] // a get-only auto-property: a store may keep its records in one
    [InlineData(true, false, false)] // a settable auto-property: an entity's navigation collection, filled by its builder
    public void IsHeldCollection_DecidesByHowTheCollectionIsHeld(bool isBackingField, bool isReadOnly, bool expected)
    {
        var dictionary = new PersistenceScan.TypeShape("", "System.Collections.Concurrent.ConcurrentDictionary`2",
            [new("", "System.String", [], false), new("A", "Ns.Record", [], false)], false);

        IsHeldCollection(new PersistenceScan.FieldShape("Entries", isBackingField, dictionary, isReadOnly)).Should().Be(expected);
    }

    [Fact]
    public void TypesThatNameAPerson_APersonTwoHoldersDeep_IsFoundWhateverOrderTheTypesAreVisitedIn()
    {
        // A store's private entry wrapping a record that wraps the owner-stamped one. The entry sorts first, so it is visited
        // before what it holds is known; a single pass would miss it.
        static PersistenceScan.TypeShape Shape(string name) => new("A", name, [], false);
        var entry = new PersistenceScan.DeclaredType("Ns.Entry", false, false, [new("Record", true, Shape("Ns.Record"))]);
        var record = new PersistenceScan.DeclaredType("Ns.Record", true, false, [new("Root", true, Shape("Ns.Root"))]);
        var root = new PersistenceScan.DeclaredType("Ns.Root", true, false, [new("UserId", true, new("", "System.String", [], false))]);

        var found = TypesThatNameAPerson([("A/Ns.Entry", entry), ("A/Ns.Record", record), ("A/Ns.Root", root)]);

        found.Should().ContainKey("A/Ns.Entry").WhoseValue.Should().Be("Ns.Root.UserId");
    }

    [Fact]
    public void EveryHolderExemption_StillMatchesWhatTheScanSees()
    {
        var holders = Holders.Value;

        HoldsOwnerRecordsWithoutStoringThem.Entries
            .Where(e => !holders.TryGetValue(e.Key, out var seen) || !seen.SetEquals(e.Value.Evidence))
            .Select(e => Suggest(e.Key, e.Value.Reason, holders.GetValueOrDefault(e.Key)))
            .Should().BeEmpty(
                "an exemption excuses the fields the scan saw when it was written; when they change, re-read the type and " +
                "declare it or update the entry");
    }

    [Fact]
    public void NoHolderExemption_IsAlsoDeclared()
    {
        HoldsOwnerRecordsWithoutStoringThem.Entries.Keys
            .Where(k => Scanned.Value.Facts.TryGetValue(k, out var type) && IsDeclared(type))
            .Should().BeEmpty("a type is either described in the inventory or excused from it, not both");
    }

    /// <summary>Every production type that keeps a collection of person-naming records, with each field that does.</summary>
    private static IReadOnlyDictionary<string, SortedSet<string>> FindHolders()
    {
        var facts = Scanned.Value.Facts;
        var namesAPerson = TypesThatNameAPerson(facts.Values
            .SelectMany(f => f.Declared.Select(d => (Key: f.Assembly + "/" + d.Name, Type: d)))
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToList());

        var holders = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var (key, type) in facts.Where(f => !f.Value.IsInterface))
        {
            foreach (var owner in type.Declared.Where(d => d is { IsRecord: false, IsCompilerGenerated: false }))
            {
                foreach (var field in owner.Fields.Where(IsHeldCollection))
                {
                    var path = field.Type.SelfAndArguments().Skip(1)
                        .Select(t => namesAPerson.GetValueOrDefault(KeyOf(t)))
                        .FirstOrDefault(p => p is not null);
                    if (path is null)
                        continue;

                    if (!holders.TryGetValue(key, out var evidence))
                        holders[key] = evidence = new SortedSet<string>(StringComparer.Ordinal);
                    evidence.Add($"{owner.Name}.{field.Name} holds {path}");
                }
            }
        }

        return holders;
    }

    /// <summary>
    /// Whether a field is a collection the type itself keeps. A get-only auto-property counts, since a store may keep its
    /// records in one. A settable one does not: that is a data shape's member, such as an entity's navigation collection,
    /// filled by whoever builds it.
    /// </summary>
    private static bool IsHeldCollection(PersistenceScan.FieldShape field) =>
        (!field.IsBackingField || field.IsReadOnly) && IsCollection(field.Type);

    private static bool IsCollection(PersistenceScan.TypeShape type) =>
        type.IsArray || (type.Arguments.Count > 0 && CollectionNamespaces.Any(ns => type.Name.StartsWith(ns, StringComparison.Ordinal)));

    private static string KeyOf(PersistenceScan.TypeShape type) => type.Assembly + "/" + type.Name;

    /// <summary>
    /// Every production type that names a person, with the field path that does, such as <c>Ns.Record.OwnerId</c>: one
    /// with an <c>OwnerId</c> or <c>UserId</c> string field, or one holding such a type in any field, so a store's private
    /// entry wrapping a record is seen.
    /// </summary>
    /// <remarks>
    /// Found as a fixed point rather than by recursing with a memo: a type that reaches a person only through a type still
    /// being decided (a child that refers back to its parent) would otherwise be recorded as naming nobody, depending on
    /// field order. Here a type is added once any type it holds is known to name a person, and the pass repeats until
    /// nothing is added. The types are visited in key order, so each one's path is the same on every run.
    /// </remarks>
    private static Dictionary<string, string> TypesThatNameAPerson(IReadOnlyList<(string Key, PersistenceScan.DeclaredType Type)> declared)
    {
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, type) in declared)
        {
            if (type.Fields.FirstOrDefault(f => PersonFields.Contains(f.Name) && f.Type.Name == "System.String") is { } field)
                paths[key] = $"{type.Name}.{field.Name}";
        }

        for (var added = true; added;)
        {
            added = false;
            foreach (var (key, type) in declared.Where(d => !paths.ContainsKey(d.Key)))
            {
                var path = type.Fields
                    .SelectMany(f => f.Type.SelfAndArguments())
                    .Select(t => paths.GetValueOrDefault(KeyOf(t)))
                    .FirstOrDefault(p => p is not null);
                if (path is null)
                    continue;

                paths[key] = path;
                added = true;
            }
        }

        return paths;
    }
}
