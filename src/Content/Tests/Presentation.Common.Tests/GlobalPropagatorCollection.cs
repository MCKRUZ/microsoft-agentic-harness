using Xunit;

namespace Presentation.Common.Tests;

/// <summary>
/// Serialises every test in this assembly that changes OpenTelemetry's <em>process-wide</em> default
/// text-map propagator, so they cannot observe or undo one another's.
/// </summary>
/// <remarks>
/// <para>
/// The propagator is a single process-global value, and xUnit runs collections in parallel. Enabling the
/// Agent 365 exporter replaces it with a trace-context-only propagator, and the tests that do so restore
/// the original afterwards — which is precisely the hazard: a restore can undo the swap while a sibling
/// test is still asserting on it, and a sibling's swap can be observed by a test expecting the default.
/// Both directions produce intermittent failures that read as product defects rather than test
/// interference.
/// </para>
/// <para>
/// <strong>One collection, not one per class.</strong> Two classes each in their own
/// <c>DisableParallelization</c> collection are serialised against each other only incidentally, with
/// nothing stating the relationship. Naming the shared hazard gives the next test that touches
/// <c>Sdk.SetDefaultTextMapPropagator</c> an obvious home and says why it belongs there.
/// </para>
/// <para>
/// Same family as <c>Infrastructure.AI.Tests</c>'s <c>ProcessEnvironmentCollection</c>, where the shared
/// state is the environment block rather than the propagator.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GlobalPropagatorCollection
{
    /// <summary>The collection name. Apply with <c>[Collection(GlobalPropagatorCollection.Name)]</c>.</summary>
    public const string Name = "GlobalPropagator";
}
