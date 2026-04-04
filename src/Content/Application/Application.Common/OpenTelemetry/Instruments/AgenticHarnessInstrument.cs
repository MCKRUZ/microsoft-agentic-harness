using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Application.Common.OpenTelemetry.Instruments;

/// <summary>
/// The harness-level <see cref="ActivitySource"/> and <see cref="Meter"/> for all custom
/// telemetry emitted by the agentic orchestration layer. All custom metrics and spans
/// created by the harness originate from these instances.
/// </summary>
/// <remarks>
/// <para>
/// This is distinct from the three AI framework instrument subscriptions in
/// <see cref="TelemetrySourceNames"/>. Those capture what the SDKs emit;
/// this emits what the harness orchestration layer produces.
/// </para>
/// <para>
/// Register via <c>builder.AddSource(TelemetrySourceNames.AgenticHarness)</c> and
/// <c>builder.AddMeter(TelemetrySourceNames.AgenticHarness)</c> in the OTel pipeline.
/// </para>
/// </remarks>
public static class AgenticHarnessInstrument
{
    private const string Version = "1.0.0";

    /// <summary>Gets the activity source for harness-level distributed tracing.</summary>
    public static ActivitySource Source { get; } = new(TelemetrySourceNames.AgenticHarness, Version);

    /// <summary>Gets the meter for harness-level metrics.</summary>
    public static Meter Meter { get; } = new(TelemetrySourceNames.AgenticHarness, Version);
}
