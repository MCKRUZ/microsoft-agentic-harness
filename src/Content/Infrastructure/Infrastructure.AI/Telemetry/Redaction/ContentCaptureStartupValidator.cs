using Application.AI.Common.Interfaces.Telemetry;
using Domain.AI.Telemetry.Conventions;
using Domain.Common.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Telemetry.Redaction;

/// <summary>
/// One-shot startup validator for the content-capture pipeline. Runs once via
/// <see cref="IHostedService.StartAsync"/> and refuses to boot the host when
/// content-capture is enabled but the surrounding configuration is unsafe.
/// </summary>
/// <remarks>
/// <para>
/// Checks performed (only when <c>AppConfig.AI.Telemetry.ContentCapture.Enabled</c>
/// is true):
/// </para>
/// <list type="number">
///   <item><description>
///     <b>SemConv stability env var pinned</b> — the harness emits against the
///     experimental OTel GenAI conventions. When content-capture is on the
///     <see cref="GenAiSemconvRegistry.SemconvStabilityOptInEnvVar"/>
///     environment variable MUST equal
///     <see cref="GenAiSemconvRegistry.SemconvStabilityOptInValue"/>; absent
///     or wrong values mean a Collector running a stable-only filter could
///     drop the content attributes silently, defeating the audit purpose.
///   </description></item>
///   <item><description>
///     <b>Redaction filter registered</b> — at least one
///     <see cref="IContentRedactionFilter"/> must be in the container. Content
///     can leave the domain only through a filter; a missing filter would
///     emit raw content the next time a toggle is flipped.
///   </description></item>
/// </list>
/// <para>
/// When content-capture is disabled the validator no-ops — the rest of the
/// graph stays inert and the env var is irrelevant to the host.
/// </para>
/// </remarks>
public sealed class ContentCaptureStartupValidator : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<ContentCaptureStartupValidator> _logger;

    /// <summary>Initializes a new <see cref="ContentCaptureStartupValidator"/>.</summary>
    /// <remarks>
    /// <see cref="IHostEnvironment"/> is resolved lazily from the service
    /// provider inside <see cref="StartAsync"/> rather than required at
    /// construction so DI tests that enumerate
    /// <c>GetServices&lt;IHostedService&gt;()</c> without a host environment
    /// don't fail at materialisation.
    /// </remarks>
    public ContentCaptureStartupValidator(
        IServiceProvider services,
        IOptionsMonitor<AppConfig> config,
        ILogger<ContentCaptureStartupValidator> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        _services = services;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var capture = _config.CurrentValue.AI.Telemetry.ContentCapture;
        if (!capture.Enabled)
        {
            return Task.CompletedTask;
        }

        ValidateSemconvStabilityEnvVar();
        ValidateRedactionFilterRegistered();

        _logger.LogInformation(
            "Content-capture enabled. Redaction categories: {Categories}; prompt={Prompt}, output={Output}, " +
            "tool.args={ToolArgs}, tool.result={ToolResult}, magentic.plan={Plan}, magentic.replan_reason={Reason}.",
            string.Join(',', capture.RedactionCategories),
            capture.CapturePromptContent,
            capture.CaptureOutputContent,
            capture.CaptureToolCallArguments,
            capture.CaptureToolCallResult,
            capture.CaptureMagenticPlanContent,
            capture.CaptureMagenticReplanReason);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static void ValidateSemconvStabilityEnvVar()
    {
        var value = Environment.GetEnvironmentVariable(GenAiSemconvRegistry.SemconvStabilityOptInEnvVar);
        if (string.Equals(value, GenAiSemconvRegistry.SemconvStabilityOptInValue, StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Content-capture is enabled (AppConfig.AI.Telemetry.ContentCapture.Enabled = true) but the " +
            $"'{GenAiSemconvRegistry.SemconvStabilityOptInEnvVar}' environment variable is not pinned to " +
            $"'{GenAiSemconvRegistry.SemconvStabilityOptInValue}'. The harness emits against the " +
            "experimental OTel GenAI conventions; without the opt-in env var, upstream Collectors may drop " +
            "content attributes silently. Set the environment variable to the expected value, or disable " +
            "AppConfig.AI.Telemetry.ContentCapture.Enabled.");
    }

    private void ValidateRedactionFilterRegistered()
    {
        var filter = _services.GetService<IContentRedactionFilter>();
        if (filter is not null)
        {
            return;
        }

        throw new InvalidOperationException(
            "Content-capture is enabled but no IContentRedactionFilter is registered in the service " +
            "container. Register DefaultContentRedactionFilter (or a custom filter) before enabling " +
            "AppConfig.AI.Telemetry.ContentCapture.Enabled.");
    }
}
