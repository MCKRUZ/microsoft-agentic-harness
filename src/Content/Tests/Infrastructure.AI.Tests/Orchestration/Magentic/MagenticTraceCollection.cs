using Xunit;

namespace Infrastructure.AI.Tests.Orchestration.Magentic;

/// <summary>
/// xUnit collection used to serialize Magentic span-emitting tests. The
/// process-global <see cref="System.Diagnostics.ActivityListener"/> registry
/// races with class-level parallelism: a listener registered in one test sees
/// activities from a peer test's <c>ActivitySource</c> until xUnit tears the
/// peer down. Putting span-emitting tests in a single collection disables
/// parallel execution between them.
/// </summary>
[CollectionDefinition("MagenticTraceCollection", DisableParallelization = true)]
public sealed class MagenticTraceCollection
{
}
