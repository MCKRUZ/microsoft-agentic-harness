using System.Reflection;
using FluentAssertions;
using Presentation.Common.Tests.Composition;
using Tests.Common;
using Xunit;

namespace Presentation.Common.Tests.Architecture;

/// <summary>
/// Every test class in this assembly that clears SQLite's connection pools runs in a collection that is not run in
/// parallel (#924).
/// </summary>
/// <remarks>
/// <para>
/// A pool-clearing class left in the parallel phase can close the idle pooled connections an end-to-end test relies
/// on staying open, which fails that test intermittently and never when it runs alone.
/// </para>
/// <para>
/// The guard reads the compiled test assembly, not the source, so it sees every call however it is written: a
/// <c>using static</c>, a method group, or a call through a helper class in this assembly, whose callers are then held
/// to the same rule. Any collection with <c>DisableParallelization</c> satisfies it, because xUnit allows a class only
/// one collection. It does not see a helper in another assembly that clears the pools.
/// </para>
/// </remarks>
public sealed class EveryPoolClearingClassIsSerialisedTests
{
    private const string ClearAllPools = "Microsoft.Data.Sqlite.SqliteConnection::ClearAllPools";

    [Fact]
    public void EveryClassThatClearsThePools_RunsInASerialisedCollection()
    {
        var assembly = typeof(EveryPoolClearingClassIsSerialisedTests).Assembly;
        var clearing = ClearingTypes(PersistenceScan.Scan([assembly.Location]));

        clearing.Should().Contain(typeof(ErasureReachesEveryStoreEndToEndTests).FullName!,
            "the scan must find the known callers, or it proves nothing");
        var serialised = SerialisedCollections(assembly);
        clearing
            .Select(name => assembly.GetType(name)!)
            .Where(type => type.GetMethods().Any(m => m.GetCustomAttribute<FactAttribute>() is not null))
            .Where(type => CollectionOf(type) is not { } name || !serialised.Contains(name))
            .Select(type => type.FullName)
            .Should().BeEmpty("a class that clears every pool must not run beside the tests that rely on idle connections");
    }

    // Direct callers, plus every type that reaches one through a helper in this assembly.
    private static HashSet<string> ClearingTypes(IReadOnlyDictionary<string, PersistenceScan.TypeFacts> facts)
    {
        var clearing = facts.Where(f => f.Value.ExternalCalls.Contains(ClearAllPools)).Select(f => f.Key).ToHashSet();
        for (var grew = true; grew;)
        {
            var reached = facts.Where(f => !clearing.Contains(f.Key) && f.Value.Callees.Overlaps(clearing))
                .Select(f => f.Key)
                .ToList();
            clearing.UnionWith(reached);
            grew = reached.Count > 0;
        }

        return clearing.Select(key => facts[key].Name).ToHashSet(StringComparer.Ordinal);
    }

    // Read from the attribute's constructor argument, the one place both xUnit versions keep the name.
    private static HashSet<string> SerialisedCollections(Assembly assembly) =>
        assembly.GetTypes()
            .SelectMany(t => t.GetCustomAttributesData())
            .Where(a => a.AttributeType == typeof(CollectionDefinitionAttribute)
                && a.NamedArguments.Any(n => n.MemberName == nameof(CollectionDefinitionAttribute.DisableParallelization)
                    && n.TypedValue.Value is true))
            .Select(a => (string)a.ConstructorArguments[0].Value!)
            .ToHashSet(StringComparer.Ordinal);

    private static string? CollectionOf(Type type) =>
        type.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType == typeof(CollectionAttribute))
            ?.ConstructorArguments[0].Value as string;
}
