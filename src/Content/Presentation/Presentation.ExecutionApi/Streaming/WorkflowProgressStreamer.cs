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

    private readonly Stream _body;

    /// <summary>Initializes a streamer writing to <paramref name="responseBody"/>.</summary>
    public WorkflowProgressStreamer(Stream responseBody)
    {
        ArgumentNullException.ThrowIfNull(responseBody);
        _body = responseBody;
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

        await foreach (var evt in subscription.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
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

    private async Task WriteAsync(WorkflowProgressEvent evt, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(evt, typeof(WorkflowProgressEvent), SerializerOptions);
        var frame = Encoding.UTF8.GetBytes($"data: {json}\n\n");

        await _body.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await _body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
