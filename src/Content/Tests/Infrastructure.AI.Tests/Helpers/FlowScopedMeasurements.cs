using System.Diagnostics.Metrics;
using Domain.Common.Telemetry;

namespace Infrastructure.AI.Tests.Helpers;

/// <summary>
/// Records measurements from the harness meter's named instruments, but only those made on the async flow
/// that created this recorder.
/// </summary>
/// <remarks>
/// The harness instruments are static, so every test in the process that touches a queue or a broker
/// reports to the same instrument at the same time. A listener that kept everything would see other
/// tests' measurements and make any exact count flaky. A measurement callback runs inline on the thread
/// that called <c>Add</c> or <c>Record</c>, carrying that caller's execution context, so an
/// <see cref="AsyncLocal{T}"/> flag set here tells this test's measurements apart from everyone else's.
/// </remarks>
internal sealed class FlowScopedMeasurements : IDisposable
{
    private static readonly AsyncLocal<FlowScopedMeasurements?> Owner = new();

    private readonly MeterListener _listener = new();
    private readonly List<Measurement> _measurements = [];
    private readonly Lock _gate = new();

    /// <summary>Starts recording the named instruments on the current async flow.</summary>
    public FlowScopedMeasurements(params string[] instrumentNames)
    {
        var names = instrumentNames.ToHashSet(StringComparer.Ordinal);
        Owner.Value = this;
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == AppInstrument.Meter.Name && names.Contains(instrument.Name))
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Capture(i, v, tags));
        _listener.SetMeasurementEventCallback<int>((i, v, tags, _) => Capture(i, v, tags));
        _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Capture(i, v, tags));
        _listener.Start();
    }

    /// <summary>The values recorded on <paramref name="instrumentName"/>, in order, optionally only those carrying a tag value.</summary>
    public IReadOnlyList<double> Values(string instrumentName, string? tagKey = null, string? tagValue = null)
    {
        lock (_gate)
        {
            return _measurements
                .Where(m => m.Instrument == instrumentName
                    && (tagKey is null || m.Tags.TryGetValue(tagKey, out var v) && Equals(v, tagValue)))
                .Select(m => m.Value)
                .ToList();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _listener.Dispose();
        Owner.Value = null;
    }

    private void Capture<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        where T : struct
    {
        if (!ReferenceEquals(Owner.Value, this))
            return;

        var tagMap = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in tags)
            tagMap[tag.Key] = tag.Value;

        lock (_gate)
            _measurements.Add(new Measurement(instrument.Name, Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture), tagMap));
    }

    private sealed record Measurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);
}
