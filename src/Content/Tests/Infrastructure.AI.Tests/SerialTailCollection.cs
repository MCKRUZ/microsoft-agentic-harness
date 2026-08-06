using Xunit;

namespace Infrastructure.AI.Tests;

/// <summary>
/// For tests that must observe the state every other test in this assembly has left behind.
/// </summary>
/// <remarks>
/// <para>
/// xUnit runs collections marked <c>DisableParallelization</c> only after every parallel collection has
/// completed. Membership here is therefore "run me once the rest of the assembly has finished" — a
/// property of the runner rather than a hope about ordering.
/// </para>
/// <para>
/// Reserve it for assertions about assembly-wide side effects. A test that merely needs isolation from
/// one specific hazard belongs in a collection that names that hazard, the way
/// <see cref="ProcessEnvironmentCollection"/> does — a catch-all serial collection would quietly become
/// the place slow or flaky tests go to hide.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialTailCollection
{
    /// <summary>The collection name. Apply with <c>[Collection(SerialTailCollection.Name)]</c>.</summary>
    public const string Name = "SerialTail";
}
