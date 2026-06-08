namespace Domain.Common.Config.AI.Telemetry;

/// <summary>
/// Telemetry-layer configuration. Today this is a thin wrapper around
/// <see cref="ContentCapture"/> (PR-11); future telemetry-only knobs live
/// under here so the <c>AppConfig.AI</c> root does not accumulate a flat
/// telemetry surface.
/// </summary>
/// <remarks>
/// Bind from <c>AppConfig:AI:Telemetry</c> in appsettings.json. The whole
/// section is optional — defaults yield content-capture OFF and no other
/// behavioural change.
/// </remarks>
public sealed class TelemetryConfig
{
    /// <summary>
    /// Per-attribute content-capture toggles. OFF by default — see
    /// <see cref="ContentCaptureConfig"/>.
    /// </summary>
    public ContentCaptureConfig ContentCapture { get; set; } = new();
}
