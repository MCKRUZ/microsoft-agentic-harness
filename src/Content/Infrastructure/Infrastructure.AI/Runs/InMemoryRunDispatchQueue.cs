using System.Threading.Channels;
using Application.AI.Common.Interfaces.Runs;

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
/// </remarks>
public sealed class InMemoryRunDispatchQueue : IRunDispatchQueue
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    /// <inheritdoc />
    public ValueTask EnqueueAsync(string jobId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        return _channel.Writer.WriteAsync(jobId, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<string> DequeueAllAsync(CancellationToken cancellationToken)
        => _channel.Reader.ReadAllAsync(cancellationToken);
}
