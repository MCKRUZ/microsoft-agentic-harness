using Application.AI.Common.Helpers;
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
    /// outright rather than replayed, treated or not. Derived from
    /// <see cref="ToolPayloadRedactor.MaxStructuralRedactionCeiling"/> — the two classes share a
    /// project, so there is no layering reason to duplicate the literal; kept as its own named
    /// constant only because this class's public surface (<c>ToolCallReplayConfigValidator</c>
    /// bounds <c>MaxVerbatimChars</c> against it) shouldn't have to know the redactor helper exists.
    /// </summary>
    public const int WithholdCeilingChars = ToolPayloadRedactor.MaxStructuralRedactionCeiling;

    private const string WithheldOversizedPlaceholder =
        "[tool result withheld from replayed history: the original output exceeded the size limit " +
        "for safe secret redaction. Re-invoke this tool if you need its output.]";

    private const string WithheldEmptyAfterSanitizationPlaceholder =
        "[tool result withheld from replayed history: its content was removed by content sanitization.]";

    private const string WithheldProcessingFailedPlaceholder =
        "[tool result withheld from replayed history: it could not be safely processed.]";

    /// <inheritdoc />
    public bool Enabled => _appConfig.CurrentValue.AI.Conversations.ToolCallReplay.Enabled;

    /// <inheritdoc />
    /// <remarks>
    /// Floored at 0 for the same reason <see cref="ResolveMaxVerbatimChars"/> clamps: an
    /// <see cref="IOptionsMonitor{TOptions}"/> value can change at runtime from a reloaded config file
    /// without going back through <c>ToolCallReplayConfigValidator</c>, and a negative limit read by a
    /// caller taking "the first N" would disable the bound rather than tighten it. Unlike that method
    /// this needs no upper clamp and logs nothing — it is a cost ceiling, not a security one, so a
    /// large value is a deployment's own trade to make and not worth a warning on every turn.
    /// </remarks>
    public int MaxCallsPerTurn =>
        Math.Max(0, _appConfig.CurrentValue.AI.Conversations.ToolCallReplay.MaxCallsPerTurn);

    /// <inheritdoc />
    /// <remarks>
    /// <para>Floored at 0 for the same reason as <see cref="MaxCallsPerTurn"/>.</para>
    /// <para>
    /// Also floored at twice the effective per-payload ceiling, which is the invariant
    /// <c>ToolCallReplayConfigValidator</c> enforces at startup — repeated here because startup is not
    /// the only way this value changes. A reloaded config file reaches
    /// <see cref="IOptionsMonitor{TOptions}"/> without going back through validation, and below that
    /// floor the window budget cannot fit even one maximum-size call: admission latches shut at the
    /// first call that does not fit, so every older call goes too and the conversation loses its whole
    /// replayed tool history rather than just the oversized entry. Raising the value silently is the
    /// safe direction here — the alternative is honouring a budget that empties the window — and it is
    /// the same defense-in-depth shape <see cref="ResolveMaxVerbatimChars"/> already applies where a
    /// reload could otherwise cross a bound that matters.
    /// </para>
    /// </remarks>
    public int MaxReplayedChars
    {
        get
        {
            var configured = Math.Max(0, _appConfig.CurrentValue.AI.Conversations.ToolCallReplay.MaxReplayedChars);
            var floor = Math.Min(int.MaxValue, (long)ResolveMaxVerbatimChars() * 2);

            return configured >= floor ? configured : (int)floor;
        }
    }

    /// <inheritdoc />
    public string NoResultPlaceholder =>
        "[no result recorded: this tool call did not complete.]";

    private readonly ICompositeResponseSanitizer _sanitizer;
    private readonly IContentRedactionFilter _redactionFilter;
    private readonly ISecretRedactor _secretRedactor;
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly ILogger<ToolCallReplayTreatment> _logger;

    /// <summary>Initializes a new instance of the <see cref="ToolCallReplayTreatment"/> class.</summary>
    /// <remarks>
    /// <paramref name="secretRedactor"/> is required, not the <c>ISecretRedactor? = null</c> a chat-client
    /// factory or turn handler accepts elsewhere in this codebase. Those are transient, human-observed
    /// exposure points (a live SSE frame, a trace-store row a human reads on a dashboard); this class is
    /// the durable, model-facing one — content it treats is written to the conversation store and fed
    /// back into the model's context on every later turn. A silently-degraded redactor here is a
    /// permanent leak, not a missed log line, so there is no safe default to fall back to.
    /// </remarks>
    public ToolCallReplayTreatment(
        ICompositeResponseSanitizer sanitizer,
        IContentRedactionFilter redactionFilter,
        ISecretRedactor secretRedactor,
        IOptionsMonitor<AppConfig> appConfig,
        ILogger<ToolCallReplayTreatment> logger)
    {
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(redactionFilter);
        ArgumentNullException.ThrowIfNull(secretRedactor);
        ArgumentNullException.ThrowIfNull(appConfig);
        ArgumentNullException.ThrowIfNull(logger);

        _sanitizer = sanitizer;
        _redactionFilter = redactionFilter;
        _secretRedactor = secretRedactor;
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

            // RE-CHECK the ceiling, on the treated text, before the structural pass below — the check
            // above was against the RAW input and does not carry over. Sanitizing and redacting can
            // GROW text (a matched secret is replaced by a longer placeholder; the injection scrubber
            // can expand too), so a sub-ceiling input can cross the ceiling right here. That matters
            // because PatternSecretRedactor silently SKIPS its structural JSON walk above this same
            // size (PatternSecretRedactor.MaxStructuralRedactionLength) and degrades to a regex-only
            // scan — which is precisely the pass that cannot see an escaped-nested-JSON secret (#391),
            // and precisely the protection the third pass below exists to add. Checking only the raw
            // length would hand an expanded payload to a redactor that quietly stopped doing the one
            // thing it was called for, and persist the result durably. Same "check, act, re-check"
            // shape ToolPayloadRedactor.RedactWithCeiling already applies on the streaming paths.
            if (treated.Length > WithholdCeilingChars)
            {
                _logger.LogWarning(
                    "[ToolCallReplayTreatment] Withholding {Tool} payload: treatment expanded it from " +
                    "{RawLength} to {TreatedLength} chars, crossing the {Ceiling}-char structural-redaction " +
                    "ceiling.",
                    toolName, rawText.Length, treated.Length, WithholdCeilingChars);
                return WithheldOversizedPlaceholder;
            }

            // A third pass, not a substitute for the two above: ICompositeResponseSanitizer and
            // IContentRedactionFilter are value-shape regex scanners with no JSON-key-name awareness —
            // neither matches a quote-terminated key like "token": or "x-api-key": (the `key\s*[=:]`
            // rules they run require an unquoted key). ToolPayloadRedactor.Redact via ISecretRedactor
            // (PatternSecretRedactor) is the structural, key-name-aware pass that already protects the
            // transient SSE stream and trace store for this identical content — this durable,
            // model-facing boundary must be at least as strong as those, not weaker. Still before the
            // cap, for the same slice-at-the-boundary reason as the pass above.
            treated = ToolPayloadRedactor.Redact(treated, _secretRedactor);

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
