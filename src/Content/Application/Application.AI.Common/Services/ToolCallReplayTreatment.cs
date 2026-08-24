using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Telemetry;
using Application.AI.Common.Services.Governance;
using Domain.AI.Telemetry.Redaction;
using Domain.Common.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Application.AI.Common.Services;

/// <inheritdoc cref="IToolCallReplayTreatment"/>
public sealed class ToolCallReplayTreatment : IToolCallReplayTreatment
{
    /// <summary>
    /// Above this size, the structural secret-redaction pass falls back to a regex-only scan that
    /// cannot see through escaped-nested-JSON secrets (#391) — a payload this large is withheld
    /// outright rather than replayed, treated or not. Mirrors
    /// <c>ToolPayloadRedactor.MaxStructuralRedactionCeiling</c>; kept as an independent constant for
    /// the same Clean Architecture reason that one is: Application must not depend on Infrastructure,
    /// where the redactor that actually enforces this ceiling lives. Change both together.
    /// </summary>
    public const int WithholdCeilingChars = 64 * 1024;

    private const string WithheldOversizedPlaceholder =
        "[tool result withheld from replayed history: the original output exceeded the size limit " +
        "for safe secret redaction. Re-invoke this tool if you need its output.]";

    private const string WithheldEmptyAfterSanitizationPlaceholder =
        "[tool result withheld from replayed history: its content was removed by content sanitization.]";

    private const string WithheldProcessingFailedPlaceholder =
        "[tool result withheld from replayed history: it could not be safely processed.]";

    /// <inheritdoc />
    public string NoResultPlaceholder =>
        "[no result recorded: this tool call did not complete.]";

    private readonly ICompositeResponseSanitizer _sanitizer;
    private readonly IContentRedactionFilter _redactionFilter;
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly ILogger<ToolCallReplayTreatment> _logger;

    /// <summary>Initializes a new instance of the <see cref="ToolCallReplayTreatment"/> class.</summary>
    public ToolCallReplayTreatment(
        ICompositeResponseSanitizer sanitizer,
        IContentRedactionFilter redactionFilter,
        IOptionsMonitor<AppConfig> appConfig,
        ILogger<ToolCallReplayTreatment> logger)
    {
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(redactionFilter);
        ArgumentNullException.ThrowIfNull(appConfig);
        ArgumentNullException.ThrowIfNull(logger);

        _sanitizer = sanitizer;
        _redactionFilter = redactionFilter;
        _appConfig = appConfig;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Treat(string rawText, string? toolName)
    {
        ArgumentNullException.ThrowIfNull(rawText);

        if (rawText.Length == 0)
            return rawText;

        try
        {
            // Size check before anything else — bounds worst-case regex-scan cost on
            // attacker-controlled text before any sanitize/redact pattern runs, and above this
            // ceiling redaction itself stops being trustworthy (see WithholdCeilingChars), so there
            // is nothing safe to do with the content but withhold it.
            if (rawText.Length > WithholdCeilingChars)
            {
                _logger.LogWarning(
                    "[ToolCallReplayTreatment] Withholding {Tool} payload: {Length} chars exceeds " +
                    "the {Ceiling}-char structural-redaction ceiling.",
                    toolName, rawText.Length, WithholdCeilingChars);
                return WithheldOversizedPlaceholder;
            }

            // Sanitize before redact, then cap last — the fixed ordering SanitizeThenRedact.Apply
            // documents and ReportedFailureText.PrepareForReporting models: capping first can slice a
            // secret at the boundary and the surviving fragment never goes back through the filter.
            var treated = SanitizeThenRedact.Apply(
                rawText, _sanitizer, _redactionFilter, RedactionCategories.All, toolName,
                onSanitizedEmpty: _ => WithheldEmptyAfterSanitizationPlaceholder);

            return BoundedText.Cap(treated, ResolveMaxVerbatimChars(), "…[truncated]").Text;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[ToolCallReplayTreatment] Failed to treat {Tool} payload for replay; withholding it.",
                toolName);
            return WithheldProcessingFailedPlaceholder;
        }
    }

    private int ResolveMaxVerbatimChars()
    {
        var configured = _appConfig.CurrentValue.AI.Conversations.ToolCallReplay.MaxVerbatimChars;
        var clamped = Math.Clamp(configured, 0, WithholdCeilingChars);

        if (clamped != configured)
        {
            // Defense in depth alongside ToolCallReplayConfigValidator's startup check — an
            // IOptionsMonitor value can change at runtime (a reloaded config file) without going
            // back through validation, and this clamp is what stops a raised ceiling from silently
            // letting unredactable content replay verbatim.
            _logger.LogWarning(
                "[ToolCallReplayTreatment] Configured MaxVerbatimChars={Configured} is outside " +
                "[0, {Ceiling}] and was clamped to {Clamped}.",
                configured, WithholdCeilingChars, clamped);
        }

        return clamped;
    }
}
