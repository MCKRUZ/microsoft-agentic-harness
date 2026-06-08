using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Telemetry;
using Microsoft.Extensions.Options;
using Moq;

namespace Infrastructure.AI.Tests.Telemetry;

/// <summary>
/// Shared builders for the content-capture test suite. Produces an
/// <see cref="IOptionsMonitor{AppConfig}"/> whose
/// <c>AI.Telemetry.ContentCapture</c> section is configured exactly the way each
/// scenario needs, so the policy, startup validator, and DI tests can assert
/// against a known config. Mirrors <c>GitOpsTestConfig</c>.
/// </summary>
internal static class ContentCaptureTestConfig
{
    /// <summary>
    /// Builds an <see cref="AppConfig"/> with the supplied content-capture
    /// section. Defaults yield the production posture: master enabled, all
    /// per-attribute toggles on, and the full category list — so a test that
    /// only cares about one toggle can flip just that toggle.
    /// </summary>
    public static AppConfig AppConfig(ContentCaptureConfig capture) => new()
    {
        AI = new AIConfig
        {
            Telemetry = new TelemetryConfig { ContentCapture = capture },
        },
    };

    /// <summary>
    /// A content-capture section with the master flag and every per-attribute
    /// toggle on, ready to be narrowed per test.
    /// </summary>
    public static ContentCaptureConfig AllOn() => new()
    {
        Enabled = true,
        CapturePromptContent = true,
        CaptureOutputContent = true,
        CaptureToolCallArguments = true,
        CaptureToolCallResult = true,
        CaptureMagenticPlanContent = true,
        CaptureMagenticReplanReason = true,
        CaptureMagenticProgressContent = true,
        CaptureMagenticPlanReviewFeedback = true,
    };

    /// <summary>Wraps an <see cref="AppConfig"/> in a Moq-backed monitor.</summary>
    public static IOptionsMonitor<AppConfig> Monitor(AppConfig appConfig)
        => Mock.Of<IOptionsMonitor<AppConfig>>(o => o.CurrentValue == appConfig);

    /// <summary>Convenience: a monitor over the supplied content-capture section.</summary>
    public static IOptionsMonitor<AppConfig> Monitor(ContentCaptureConfig capture)
        => Monitor(AppConfig(capture));
}
