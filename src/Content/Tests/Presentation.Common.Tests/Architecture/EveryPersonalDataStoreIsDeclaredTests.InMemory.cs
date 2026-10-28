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
/// records past one call: records and auto-property backing fields (a value or a DTO, not a store), compiler-generated
/// closures and state machines, and a single record held in a field rather than a collection of them. A holder must be
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
        var declared = facts.Values
            .SelectMany(f => f.Declared.Select(d => (Key: f.Assembly + "/" + d.Name, Type: d)))
            .ToDictionary(x => x.Key, x => x.Type, StringComparer.Ordinal);
        var names = new Dictionary<string, string?>(StringComparer.Ordinal);

        var holders = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var (key, type) in facts.Where(f => !f.Value.IsInterface))
        {
            foreach (var owner in type.Declared.Where(d => d is { IsRecord: false, IsCompilerGenerated: false }))
            {
                foreach (var field in owner.Fields.Where(f => !f.IsBackingField && IsCollection(f.Type)))
                {
                    var path = field.Type.SelfAndArguments().Skip(1)
                        .Select(t => NamesAPerson(t, declared, names))
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

    private static bool IsCollection(PersistenceScan.TypeShape type) =>
        type.IsArray || (type.Arguments.Count > 0 && CollectionNamespaces.Any(ns => type.Name.StartsWith(ns, StringComparison.Ordinal)));

    /// <summary>
    /// The field path by which a production type names a person, such as <c>Ns.Record.OwnerId</c>, or null when it does
    /// not; through the types its fields hold, so a store's private entry wrapping a record is seen.
    /// </summary>
    private static string? NamesAPerson(
        PersistenceScan.TypeShape shape,
        IReadOnlyDictionary<string, PersistenceScan.DeclaredType> declared,
        Dictionary<string, string?> memo)
    {
        var key = shape.Assembly + "/" + shape.Name;
        if (memo.TryGetValue(key, out var known))
            return known;
        if (!declared.TryGetValue(key, out var type))
            return null;

        memo[key] = null; // A type that holds itself (a tree node) is decided by its other fields.
        var path = type.Fields
            .Select(f => PersonFields.Contains(f.Name) && f.Type.Name == "System.String"
                ? $"{type.Name}.{f.Name}"
                : f.Type.SelfAndArguments().Select(t => NamesAPerson(t, declared, memo)).FirstOrDefault(p => p is not null))
            .FirstOrDefault(p => p is not null);
        memo[key] = path;
        return path;
    }
}
