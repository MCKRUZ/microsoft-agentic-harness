using System.Diagnostics.Metrics;
using Domain.Common.Telemetry;

namespace Presentation.AgentHub.Tests.Telemetry;

/// <summary>
/// Watches one up-down counter on the harness meter and records both how many measurements it
/// published and what they add up to.
/// </summary>
/// <remarks>
/// <para>
/// Both numbers matter, and a test that checks only the total is the trap this exists to avoid: a
/// gauge that is never touched at all also nets to zero. <see cref="Measurements"/> says the code
/// under test reached the instrument; <see cref="Net"/> says it left it where it found it.
/// </para>
/// <para>
/// It listens to the real instrument rather than mocking one, because the instrument is the thing
/// being got wrong. The defect behind issue #289 was a gauge the AG-UI path incremented and had no
/// decrement for anywhere — so it rose forever — and every unit test over that path passed the whole
/// time, because not one of them could observe the instrument.
/// </para>
/// <para>
/// The harness meter is a process-wide static, so a probe sees measurements from anything running
/// concurrently. That is safe here only because this assembly is serialised (issue #261) for exactly
/// this reason; a probe used from a parallel collection would report other tests' work as its own.
/// </para>
/// </remarks>
public sealed class GaugeProbe : IDisposable
{
    private readonly MeterListener _listener;
    private long _net;
    private long _measurements;

    /// <summary>Starts watching <paramref name="instrumentName"/> on the harness meter.</summary>
    /// <param name="instrumentName">
    /// The OpenTelemetry instrument name, e.g. <c>agent.orchestration.runs_active</c> — not the
    /// Prometheus name it is exported under.
    /// </param>
    public GaugeProbe(string instrumentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instrumentName);

        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == AppSourceNames.AgenticHarness
                    && instrument.Name == instrumentName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };

        _listener.SetMeasurementEventCallback<int>((_, measurement, _, _) =>
        {
            Interlocked.Add(ref _net, measurement);
            Interlocked.Increment(ref _measurements);
        });

        // Start replays instruments that already exist, which every one of these does: they are static
        // properties initialised the first time their class is touched, long before any test runs.
        _listener.Start();
    }

    /// <summary>The sum of every measurement seen. Zero means the gauge ended where it started.</summary>
    public long Net => Interlocked.Read(ref _net);

    /// <summary>How many measurements were seen at all.</summary>
    public long Measurements => Interlocked.Read(ref _measurements);

    /// <inheritdoc />
    public void Dispose() => _listener.Dispose();
}
