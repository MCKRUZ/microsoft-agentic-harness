using System.Diagnostics.Metrics;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Telemetry;

namespace Application.AI.Common.OpenTelemetry.Metrics;

/// <summary>
/// OTel instruments for background dispatch: how much accepted work is waiting for a dispatch slot, how long
/// it waited, and how many workflow progress streams are open.
/// </summary>
/// <remarks>
/// Static instruments on the shared harness meter, like every other metrics class here, so a host that
/// builds more than one dispatcher (or a test process that builds many hosts) still reports one series per
/// queue rather than one per instance. See <see cref="DispatchConventions"/> for what each one measures.
/// </remarks>
public static class DispatchMetrics
{
    /// <summary>Items waiting in a dispatch queue. Tags: agent.dispatch.queue.</summary>
    public static UpDownCounter<long> QueueDepth { get; } =
        AppInstrument.Meter.CreateUpDownCounter<long>(DispatchConventions.QueueDepth, "{item}", "Items waiting in a dispatch queue");

    /// <summary>Time from entering a dispatch queue to being taken by its dispatcher. Tags: agent.dispatch.queue.</summary>
    public static Histogram<double> QueueWait { get; } =
        AppInstrument.Meter.CreateHistogram<double>(DispatchConventions.QueueWait, "{ms}", "Time an item waited in a dispatch queue");

    /// <summary>Workflow progress streams currently open.</summary>
    public static UpDownCounter<int> ProgressStreamsActive { get; } =
        AppInstrument.Meter.CreateUpDownCounter<int>(DispatchConventions.ProgressStreamsActive, "{stream}", "Workflow progress streams currently open");
}
