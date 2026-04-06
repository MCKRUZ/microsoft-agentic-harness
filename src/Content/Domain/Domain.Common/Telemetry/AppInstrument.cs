using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Domain.Common.Telemetry;

/// <summary>
/// The application-level <see cref="ActivitySource"/> and <see cref="Meter"/> for all custom
/// telemetry emitted by the harness.
/// </summary>
public static class AppInstrument
{
    private const string Version = "1.0.0";

    /// <summary>Gets the activity source for harness-level distributed tracing.</summary>
    public static ActivitySource Source { get; } = new(AppSourceNames.AgenticHarness, Version);

    /// <summary>Gets the meter for harness-level metrics.</summary>
    public static Meter Meter { get; } = new(AppSourceNames.AgenticHarness, Version);
}
