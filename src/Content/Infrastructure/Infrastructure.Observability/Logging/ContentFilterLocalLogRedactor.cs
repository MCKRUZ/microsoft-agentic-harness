using System.Collections.Immutable;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Telemetry;
using Application.AI.Common.Services.Governance;
using Application.Common.Logging;
using Domain.AI.Telemetry.Redaction;
using Domain.Common.Config.Observability;
using Infrastructure.Observability.Processors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Observability.Logging;

/// <summary>
/// The one <see cref="ILocalLogRedactor"/> implementation: bridges the same
/// <see cref="IContentRedactionFilter"/> and <see cref="LogsConfig"/> the OTel logging bridge's
/// <see cref="LogRecordRedactionProcessor"/> already uses, so local sinks (console, file, JSONL, named
/// pipe) and the OTel bridge share one redaction-enabled/categories knob (#457) instead of needing two.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Widens what <see cref="LogsConfig.RedactionEnabled"/>/<see cref="LogsConfig.RedactionCategories"/>
/// govern.</strong> Both were previously read only inside <see cref="LogRecordRedactionProcessor"/>,
/// itself only constructed when <see cref="LogsConfig.OtelExportEnabled"/> is on — so in practice they
/// gated the OTel bridge alone. This type reads them independently of that flag: local sinks exist (and
/// need redacting) whether or not the OTel bridge is switched on, so the same operator intent
/// ("scrub PII/secrets from logs") now reaches both surfaces from one setting.
/// </para>
/// <para>
/// <strong>Sanitizes before redacting (via <see cref="SanitizeThenRedact"/>), same as #470.</strong>
/// This was the one local-sink redaction path that shipped without it — found in review: without the
/// sanitize step, a secret split by invisible/zero-width characters (which the sanitizer canonicalizes
/// away, but the redaction filter's anchored patterns do not) could dodge redaction here specifically,
/// the exact evasion #470 closed for the OTel span paths.
/// </para>
/// </remarks>
public sealed class ContentFilterLocalLogRedactor : ILocalLogRedactor
{
    private readonly ICompositeResponseSanitizer _sanitizer;
    private readonly IContentRedactionFilter _filter;
    private readonly IOptionsMonitor<LogsConfig> _config;
    private readonly ILogger<ContentFilterLocalLogRedactor> _logger;

    public ContentFilterLocalLogRedactor(
        ICompositeResponseSanitizer sanitizer,
        IContentRedactionFilter filter,
        IOptionsMonitor<LogsConfig> config,
        ILogger<ContentFilterLocalLogRedactor> logger)
    {
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _sanitizer = sanitizer;
        _filter = filter;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool Enabled => _config.CurrentValue.RedactionEnabled;

    /// <inheritdoc />
    public string Redact(string text) =>
        // ResolveCategories always returns a non-empty set (RedactionCategoryParser's fail-safe-not-open
        // fallback guarantees it whenever this is called with enabled: true, which it always is here).
        SanitizeThenRedact.Apply(text, _sanitizer, _filter, ResolveCategories());

    /// <summary>
    /// Re-parses on every call rather than caching: <see cref="LogsConfig"/> is bound from
    /// <see cref="IOptionsMonitor{TOptions}"/>, so an operator can change the configured categories at
    /// runtime and this must pick it up the same way <see cref="LogRecordRedactionProcessor"/> would on
    /// its own next construction. Cheap regardless — the configured list is a handful of strings, not a
    /// hot-path allocation worth guarding.
    /// </summary>
    private ImmutableArray<RedactionCategory> ResolveCategories() =>
        // The fail-safe-not-open fallback (empty-but-enabled → every category) lives once in
        // RedactionCategoryParser, shared with LogRecordRedactionProcessor. `enabled: true` here
        // because the caller (Redact) only reaches this after checking Enabled itself.
        RedactionCategoryParser.Parse(_config.CurrentValue.RedactionCategories, _logger, enabled: true);
}
