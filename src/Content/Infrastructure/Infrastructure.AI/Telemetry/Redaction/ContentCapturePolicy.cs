using Application.AI.Common.Interfaces.Telemetry;
using Domain.AI.Telemetry.Redaction;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Telemetry.Redaction;

/// <summary>
/// Default <see cref="IContentCapturePolicy"/>. Reads
/// <c>AppConfig.AI.Telemetry.ContentCapture</c> on each call so a runtime
/// <c>IOptionsMonitor</c> update flips the toggles without a host restart.
/// </summary>
/// <remarks>
/// <para>
/// When <c>ContentCapture.Enabled</c> is false every <c>ShouldCapture*</c>
/// method returns false regardless of the per-attribute flags. The master
/// flag is the single decision the operator has to flip to turn capture on;
/// individual attributes then opt in.
/// </para>
/// <para>
/// <see cref="Categories"/> is recomputed on each read. Unknown category
/// names in <c>ContentCapture.RedactionCategories</c> are skipped with a
/// debug log so a config typo is loud during development without bringing
/// the host down.
/// </para>
/// </remarks>
public sealed class ContentCapturePolicy : IContentCapturePolicy
{
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly ILogger<ContentCapturePolicy> _logger;

    /// <summary>Initializes the policy.</summary>
    public ContentCapturePolicy(
        IOptionsMonitor<AppConfig> config,
        ILogger<ContentCapturePolicy> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsEnabled => _config.CurrentValue.AI.Telemetry.ContentCapture.Enabled;

    /// <inheritdoc />
    public IReadOnlyList<RedactionCategory> Categories
    {
        get
        {
            var capture = _config.CurrentValue.AI.Telemetry.ContentCapture;
            var result = new List<RedactionCategory>(capture.RedactionCategories.Count);
            foreach (var name in capture.RedactionCategories)
            {
                if (Enum.TryParse<RedactionCategory>(name, ignoreCase: true, out var category))
                {
                    result.Add(category);
                }
                else
                {
                    _logger.LogDebug(
                        "Unknown content-capture redaction category '{Category}' — ignored.",
                        name);
                }
            }
            return result;
        }
    }

    /// <inheritdoc />
    public bool ShouldCapturePromptContent()
        => IsEnabled && _config.CurrentValue.AI.Telemetry.ContentCapture.CapturePromptContent;

    /// <inheritdoc />
    public bool ShouldCaptureOutputContent()
        => IsEnabled && _config.CurrentValue.AI.Telemetry.ContentCapture.CaptureOutputContent;

    /// <inheritdoc />
    public bool ShouldCaptureToolCallArguments()
        => IsEnabled && _config.CurrentValue.AI.Telemetry.ContentCapture.CaptureToolCallArguments;

    /// <inheritdoc />
    public bool ShouldCaptureToolCallResult()
        => IsEnabled && _config.CurrentValue.AI.Telemetry.ContentCapture.CaptureToolCallResult;

    /// <inheritdoc />
    public bool ShouldCaptureMagenticPlanContent()
        => IsEnabled && _config.CurrentValue.AI.Telemetry.ContentCapture.CaptureMagenticPlanContent;

    /// <inheritdoc />
    public bool ShouldCaptureMagenticReplanReason()
        => IsEnabled && _config.CurrentValue.AI.Telemetry.ContentCapture.CaptureMagenticReplanReason;

    /// <inheritdoc />
    public bool ShouldCaptureMagenticProgressContent()
        => IsEnabled && _config.CurrentValue.AI.Telemetry.ContentCapture.CaptureMagenticProgressContent;

    /// <inheritdoc />
    public bool ShouldCaptureMagenticPlanReviewFeedback()
        => IsEnabled && _config.CurrentValue.AI.Telemetry.ContentCapture.CaptureMagenticPlanReviewFeedback;
}
