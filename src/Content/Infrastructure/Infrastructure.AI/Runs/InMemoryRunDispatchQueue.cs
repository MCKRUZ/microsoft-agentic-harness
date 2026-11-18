using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Telemetry.Conventions;
using Infrastructure.AI.Helpers;

namespace Infrastructure.AI.Runs;

/// <summary>
/// Process-local <see cref="IRunDispatchQueue"/> over an unbounded channel.
/// </summary>
/// <remarks>
/// <para>
/// Unbounded is safe here only because admission bounds what can reach it: a caller cannot enqueue
/// beyond its concurrent-run cap, and each enqueue costs a rate-limited request. The queue is not the
/// backpressure mechanism and must not become one — if the caps are ever removed, this needs a bound.
/// </para>
/// <para>
/// Single reader, many writers: one dispatcher drains it while any number of request threads enqueue.
/// </para>
/// <para>
/// Its depth and wait are reported through <see cref="InstrumentedDispatchChannel"/>.
/// </para>
/// </remarks>
public sealed class InMemoryRunDispatchQueue : IRunDispatchQueue
{
    private readonly InstrumentedDispatchChannel _channel = new(DispatchConventions.WorkflowRunQueue);

    /// <inheritdoc />
    public ValueTask EnqueueAsync(string jobId, CancellationToken cancellationToken)
        => _channel.EnqueueAsync(jobId, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<string> DequeueAllAsync(CancellationToken cancellationToken)
        => _channel.DequeueAllAsync(cancellationToken);
}
