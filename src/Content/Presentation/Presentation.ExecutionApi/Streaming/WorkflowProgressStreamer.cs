using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Application.AI.Common.Interfaces.Runs;
using Domain.AI.Runs;

namespace Presentation.ExecutionApi.Streaming;

/// <summary>
/// Writes one run's progress to a response body as Server-Sent-Events.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Snapshot first, then live.</strong> Nothing is buffered for a watcher who has not arrived,
/// so a stream that opened only live events would show an apparently idle run until the next step
/// happened to finish — and nothing at all for a run that had already ended. The snapshot makes the
/// stream truthful from its first frame about what it can and cannot show.
/// </para>
/// <para>
/// <strong>The caller subscribes before reading the state this reports.</strong> The other order has
/// a window in which the run advances after the snapshot but before anyone is listening, and those
/// events are lost with nothing to indicate it — invisible to the client, because a gap it never saw
/// the far side of looks like nothing happening. Subscribing first makes the worst case an event the
/// client sees twice, which its sequence numbers make obvious.
/// </para>
/// </remarks>
public sealed class WorkflowProgressStreamer
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// How long a stream may stay silent before it says something. Short enough to sit well inside the
    /// idle timeouts intermediaries commonly apply, long enough to cost nothing.
    /// </summary>
    public static readonly TimeSpan DefaultKeepAliveInterval = TimeSpan.FromSeconds(15);

    /// <summary>An SSE comment. Conformant clients ignore it; intermediaries see traffic.</summary>
    private const string KeepAliveFrame = ": keep-alive\n\n";

    private readonly Stream _body;
    private readonly TimeSpan _keepAliveInterval;

    /// <summary>Initializes a streamer writing to <paramref name="responseBody"/>.</summary>
    /// <param name="responseBody">Where frames are written.</param>
    /// <param name="keepAliveInterval">
    /// How long the stream may stay silent before emitting a keep-alive comment. Defaults to
    /// <see cref="DefaultKeepAliveInterval"/>. Injectable because the quiet path is otherwise only
    /// reachable by a test that waits out the production interval, which is long enough that nobody
    /// writes that test — and the quiet path is where this class is least like the busy one.
    /// </param>
    public WorkflowProgressStreamer(Stream responseBody, TimeSpan? keepAliveInterval = null)
    {
        ArgumentNullException.ThrowIfNull(responseBody);

        _body = responseBody;
        _keepAliveInterval = keepAliveInterval ?? DefaultKeepAliveInterval;
    }

    /// <summary>
    /// Streams <paramref name="run"/>'s progress until it finishes, the client disconnects, or the
    /// host stops.
    /// </summary>
    /// <param name="run">The run as it stood when the request was authorized.</param>
    /// <param name="subscription">Live events for that run. Owned by the caller.</param>
    /// <param name="cancellationToken">Ends the stream.</param>
    public async Task StreamAsync(
        RunRecord run, IRunProgressSubscription subscription, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(subscription);

        await WriteAsync(
            new WorkflowProgressSnapshotEvent(run.JobId, run.TargetId, run.Status.ToString(), run.IsTerminal),
            cancellationToken).ConfigureAwait(false);

        // A run that had already finished has nothing further to report, and waiting on its stream
        // would hold the connection open until the client gave up.
        if (run.IsTerminal)
            return;

        var reportedDrops = 0L;

        await foreach (var evt in HeartbeatAsync(subscription, cancellationToken).ConfigureAwait(false))
        {
            // A heartbeat is not an event; it has already been written as an SSE comment to keep the
            // connection observably alive.
            if (evt is null)
                continue;

            // Checked per frame rather than once: a watcher can start keeping up again, and the count
            // only matters when it has grown since the client was last told.
            var dropped = subscription.DroppedCount;
            if (dropped > reportedDrops)
            {
                reportedDrops = dropped;
                await WriteAsync(new WorkflowProgressGapEvent(dropped), cancellationToken).ConfigureAwait(false);
            }

            var frame = WorkflowProgressEventMapper.ToFrame(evt);
            if (frame is not null)
                await WriteAsync(frame, cancellationToken).ConfigureAwait(false);

            if (evt.Kind == RunProgressKind.RunFinished)
                return;
        }
    }

    /// <summary>
    /// Yields the subscription's events, emitting a keep-alive comment whenever it goes quiet.
    /// </summary>
    /// <remarks>
    /// A workflow step can run for minutes without producing anything to report, and a stream that
    /// sends no bytes for that long is indistinguishable from a dead one: proxies and load balancers
    /// with an idle-read timeout close it, and the client sees the run abandoned rather than running.
    /// An SSE comment is ignored by every conformant client, so this costs the caller nothing to
    /// consume — it exists so both ends can tell a quiet stream from a broken one.
    /// </remarks>
    private async IAsyncEnumerable<RunProgressEvent?> HeartbeatAsync(
        IRunProgressSubscription subscription,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var events = subscription.ReadAllAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);

        try
        {
            // Held across iterations rather than started fresh each time round. When the keep-alive
            // wins the race below, this read is still outstanding — asking the enumerator for another
            // one while the first has not finished is two concurrent MoveNextAsync calls on a single
            // async enumerator, which is undefined and in practice corrupts its state machine. It
            // faulted on a thread-pool thread with nobody left to observe it, which ends the process,
            // and every step quieter than the keep-alive interval took that path: the ordinary case
            // for real work, not an edge one. The pending read is reused until it actually completes.
            Task<bool>? next = null;

            while (true)
            {
                next ??= events.MoveNextAsync().AsTask();

                // The delay deliberately takes no cancellation token. Whichever task loses this race
                // is abandoned, and an abandoned cancellable delay faults when the token fires — with
                // nobody left to observe it. That is an unhandled exception on a thread-pool thread
                // every time a client disconnects, which is the ordinary way a stream ends. The read
                // already honours cancellation, so the token is not needed here to stop the loop.
                var keepAlive = Task.Delay(_keepAliveInterval);
                var completed = await Task.WhenAny(next, keepAlive).ConfigureAwait(false);

                if (completed != next)
                {
                    // A client that has gone is the ordinary way a stream ends — a closed tab, a
                    // dropped connection — and the first the server hears of it is often a failed
                    // write. Ending quietly is the honest response: the run is unaffected, and there
                    // is nobody left to tell.
                    if (!await TryWriteRawAsync(KeepAliveFrame, cancellationToken).ConfigureAwait(false))
                        yield break;

                    yield return null;
                    continue;
                }

                if (!await next.ConfigureAwait(false))
                    yield break;

                var current = events.Current;

                // Cleared only now the read has completed and its value has been taken, so the next
                // iteration is the only place a fresh one is ever started.
                next = null;

                yield return current;
            }
        }
        finally
        {
            await events.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Writes a raw frame, reporting whether the client was still there to receive it.</summary>
    private async Task<bool> TryWriteRawAsync(string frame, CancellationToken cancellationToken)
    {
        try
        {
            await _body.WriteAsync(Encoding.UTF8.GetBytes(frame), cancellationToken).ConfigureAwait(false);
            await _body.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException)
        {
            // The three ways a gone client presents itself. Anything else is a real fault and is left
            // to propagate rather than being swallowed as a disconnect.
            return false;
        }
    }

    private async Task WriteAsync(WorkflowProgressEvent evt, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(evt, typeof(WorkflowProgressEvent), SerializerOptions);
        var frame = Encoding.UTF8.GetBytes($"data: {json}\n\n");

        await _body.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await _body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
