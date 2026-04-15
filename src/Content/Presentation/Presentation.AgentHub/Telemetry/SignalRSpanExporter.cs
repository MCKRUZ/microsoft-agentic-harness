using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using Presentation.AgentHub.Hubs;

namespace Presentation.AgentHub.Telemetry;

/// <summary>
/// Custom OpenTelemetry exporter that bridges the OTel Activity pipeline to SignalR clients in real time.
/// Implements <see cref="BaseExporter{T}"/> for synchronous export on the OTel SDK thread and
/// <see cref="IHostedService"/> for asynchronous drain to SignalR on a dedicated background loop.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Export"/> is called synchronously from the OTel SDK background thread where <c>await</c>
/// is unavailable. Calling SignalR's <c>SendAsync</c> directly would block the pipeline under load.
/// Instead, <see cref="Export"/> enqueues <see cref="SpanData"/> to a bounded
/// <see cref="Channel{T}"/> via non-blocking <c>TryWrite</c>.
/// </para>
/// <para>
/// <see cref="StartAsync"/> launches <see cref="DrainAsync"/> which dequeues spans and calls
/// <c>SendAsync</c> using <c>await Task.WhenAll</c> — never <c>Task.Run</c> — to preserve ordering
/// and surface exceptions without unbounded task spawning.
/// </para>
/// <para>
/// The channel is bounded at 1000 with <see cref="BoundedChannelFullMode.DropOldest"/>.
/// When the channel is at capacity a warning is logged and the oldest span is silently replaced,
/// ensuring backpressure never stalls the agent pipeline.
/// </para>
/// </remarks>
public sealed class SignalRSpanExporter : BaseExporter<Activity>, IHostedService
{
    private const int ChannelCapacity = 1000;

    private readonly IHubContext<AgentTelemetryHub> _hubContext;
    private readonly ILogger<SignalRSpanExporter> _logger;
    private readonly Channel<SpanData> _channel;
    private Task _drainTask = Task.CompletedTask;

    /// <summary>
    /// Initializes a new instance of <see cref="SignalRSpanExporter"/>.
    /// </summary>
    /// <param name="hubContext">SignalR hub context used to broadcast spans to connected clients.</param>
    /// <param name="logger">Logger for backpressure warnings.</param>
    public SignalRSpanExporter(
        IHubContext<AgentTelemetryHub> hubContext,
        ILogger<SignalRSpanExporter> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
        _channel = Channel.CreateBounded<SpanData>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
    }

    /// <summary>
    /// Called synchronously by the OTel SDK. Writes each span to the bounded channel.
    /// Never blocks — if the channel is at capacity the oldest span is dropped and a warning is logged.
    /// Always returns <see cref="ExportResult.Success"/>.
    /// </summary>
    public override ExportResult Export(in Batch<Activity> batch)
    {
        foreach (var activity in batch)
        {
            var data = MapToSpanData(activity);
            var wasFull = _channel.Reader.Count >= ChannelCapacity;
            _channel.Writer.TryWrite(data);
            if (wasFull)
            {
                _logger.LogWarning(
                    "OTel channel full — span dropped: {SpanName}", activity.DisplayName);
            }
        }
        return ExportResult.Success;
    }

    /// <summary>
    /// Converts an <see cref="Activity"/> to a <see cref="SpanData"/> record.
    /// Sets <see cref="SpanData.ParentSpanId"/> to <see langword="null"/> when
    /// <c>activity.ParentSpanId == default(<see cref="ActivitySpanId"/>)</c> (root span).
    /// Extracts the <c>agent.conversation_id</c> tag into <see cref="SpanData.ConversationId"/>.
    /// </summary>
    internal static SpanData MapToSpanData(Activity activity)
    {
        var parentSpanId = activity.ParentSpanId == default(ActivitySpanId)
            ? null
            : activity.ParentSpanId.ToHexString();

        var conversationId = activity.GetTagItem("agent.conversation_id") as string;

        var status = activity.Status switch
        {
            ActivityStatusCode.Ok => "ok",
            ActivityStatusCode.Error => "error",
            _ => "unset"
        };

        var kind = activity.Kind switch
        {
            ActivityKind.Client => "client",
            ActivityKind.Server => "server",
            _ => "internal"
        };

        var tags = activity.Tags
            .Where(t => t.Value is not null)
            .ToDictionary(t => t.Key, t => t.Value!)
            as IReadOnlyDictionary<string, string>
            ?? new Dictionary<string, string>();

        return new SpanData(
            Name: activity.DisplayName,
            TraceId: activity.TraceId.ToHexString(),
            SpanId: activity.SpanId.ToHexString(),
            ParentSpanId: parentSpanId,
            ConversationId: conversationId,
            StartTime: activity.StartTimeUtc,
            DurationMs: activity.Duration.TotalMilliseconds,
            Status: status,
            StatusDescription: activity.StatusDescription,
            Kind: kind,
            SourceName: activity.Source.Name,
            Tags: tags
        );
    }

    /// <summary>
    /// Starts the background drain loop. Reads spans from the channel and broadcasts via SignalR.
    /// Uses <c>await Task.WhenAll</c> per span to preserve ordering and propagate exceptions.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _drainTask = DrainAsync(cancellationToken);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Signals the drain loop to stop by completing the channel writer, then awaits the drain task.
    /// Safe to call multiple times.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        await _drainTask;
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        await foreach (var span in _channel.Reader.ReadAllAsync(ct))
        {
            try
            {
                var tasks = new List<Task>();

                if (span.ConversationId is not null)
                {
                    tasks.Add(_hubContext.Clients
                        .Group(AgentTelemetryHub.ConversationGroup(span.ConversationId))
                        .SendAsync(AgentTelemetryHub.EventSpanReceived, span, ct));
                }

                tasks.Add(_hubContext.Clients
                    .Group(AgentTelemetryHub.GlobalTracesGroup)
                    .SendAsync(AgentTelemetryHub.EventSpanReceived, span, ct));

                await Task.WhenAll(tasks);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to broadcast span {SpanName} via SignalR", span.Name);
            }
        }
    }
}
