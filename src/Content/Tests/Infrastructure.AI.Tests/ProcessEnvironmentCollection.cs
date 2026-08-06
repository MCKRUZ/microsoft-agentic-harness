using Xunit;

namespace Infrastructure.AI.Tests;

/// <summary>
/// Serialises every test in this assembly that reads or writes a <em>process-wide</em> environment
/// variable, so they cannot observe or clear one another's.
/// </summary>
/// <remarks>
/// <para>
/// Environment variables are per-process, not per-test, and xUnit runs collections in parallel. A test
/// that sets a variable and clears it in a <c>finally</c> can therefore clear it out from under a
/// sibling still running, or a sibling can observe one it expected to be absent. Both failures are
/// intermittent and look like product defects — one of them named a sandbox isolation control and read
/// as a security regression on first sight (issue #269).
/// </para>
/// <para>
/// <strong>One collection, not one per class.</strong> Two classes each in their own
/// <c>DisableParallelization</c> collection are serialised against everything, which happens to include
/// each other — but only incidentally, and nothing states the relationship. Naming the shared hazard
/// gives the next test that touches <c>Environment.SetEnvironmentVariable</c> an obvious home and says
/// why it belongs there.
/// </para>
/// <para>
/// Same family as the <c>Presentation.AgentHub.Tests</c> assembly-wide serialisation (issue #261), where
/// the shared state is OpenTelemetry's global meter rather than the environment block.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    /// <summary>The collection name. Apply with <c>[Collection(ProcessEnvironmentCollection.Name)]</c>.</summary>
    public const string Name = "ProcessEnvironment";
}
