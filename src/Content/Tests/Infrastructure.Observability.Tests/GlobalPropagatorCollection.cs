using Xunit;

namespace Infrastructure.Observability.Tests;

/// <summary>
/// Serialises every test in this assembly that reads or changes OpenTelemetry's <em>process-wide</em>
/// default text-map propagator, so they cannot observe or undo one another's.
/// </summary>
/// <remarks>
/// <para>
/// The propagator is a single process-global value, and xUnit runs collections in parallel.
/// <see cref="Infrastructure.Observability.Agent365.Agent365StartupValidator"/> reads it to re-assert
/// the baggage-egress policy
/// (#738) — a test that sets a composite propagator to prove the throw path, run in parallel with any
/// other test, can leak that composite propagator into a sibling asserting on the trace-context-only
/// default, or vice versa. Both directions produce intermittent failures that read as product defects
/// rather than test interference.
/// </para>
/// <para>
/// Same family as <c>Presentation.Common.Tests</c>'s <c>GlobalPropagatorCollection</c> and
/// <c>Infrastructure.AI.Tests</c>'s <c>ProcessEnvironmentCollection</c> — xUnit collections are scoped
/// per assembly, so this assembly needs its own rather than sharing theirs.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GlobalPropagatorCollection
{
    /// <summary>The collection name. Apply with <c>[Collection(GlobalPropagatorCollection.Name)]</c>.</summary>
    public const string Name = "GlobalPropagator";
}
