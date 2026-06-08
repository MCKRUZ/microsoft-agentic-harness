using Application.AI.Common.Interfaces.Telemetry;
using Infrastructure.AI.Telemetry.Redaction;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Infrastructure.AI;

public static partial class DependencyInjection
{
    /// <summary>
    /// Registers the OTel GenAI content-capture pipeline (PR-11): the
    /// per-attribute capture policy, the default regex-based redaction
    /// filter, and the startup validator that fails-loud at boot when
    /// content-capture is enabled but
    /// <c>OTEL_SEMCONV_STABILITY_OPT_IN</c> is not pinned to
    /// <see cref="Domain.AI.Telemetry.Conventions.GenAiSemconvRegistry.SemconvStabilityOptInValue"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All registrations are unconditional. The pipeline is inert until the
    /// consumer flips <c>AppConfig.AI.Telemetry.ContentCapture.Enabled</c>
    /// and toggles individual attribute flags — the policy returns false for
    /// every <c>ShouldCapture*</c> call until that happens, so span emitters
    /// never invoke the filter and never attach content attributes.
    /// </para>
    /// <para>
    /// <see cref="IContentRedactionFilter"/> is registered via
    /// <c>TryAddSingleton</c> so a consumer can register a custom filter
    /// before <c>AddInfrastructureAIDependencies</c> runs without colliding
    /// with the built-in default.
    /// </para>
    /// </remarks>
    private static void RegisterContentCaptureServices(IServiceCollection services)
    {
        services.TryAddSingleton<IContentRedactionFilter, DefaultContentRedactionFilter>();
        services.TryAddSingleton<IContentCapturePolicy, ContentCapturePolicy>();
        services.AddHostedService<ContentCaptureStartupValidator>();
    }
}
