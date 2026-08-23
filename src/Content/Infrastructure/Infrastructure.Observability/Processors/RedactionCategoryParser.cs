using System.Collections.Immutable;
using Domain.AI.Telemetry.Redaction;
using Domain.Common.Helpers;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Observability.Processors;

/// <summary>
/// Parses <c>LogsConfig.RedactionCategories</c>' configured names to <see cref="RedactionCategory"/>
/// values, and applies the fail-safe-not-open fallback that decision needs — shared by
/// <see cref="LogRecordRedactionProcessor"/> and the local-sink redactor (#457) so the two logging
/// surfaces this one config section drives read it, and its fallback policy, identically.
/// </summary>
internal static class RedactionCategoryParser
{
    /// <summary>
    /// Parses the configured category names, skipping (and logging) any name the enum does not
    /// recognise, then — when <paramref name="enabled"/> and nothing resolved — falls back to every
    /// category rather than returning an empty set.
    /// </summary>
    /// <remarks>
    /// Startup validation (<c>LogsConfigValidator</c>) already rejects an enabled-but-empty/unknown
    /// category set on every host that wires <c>ValidateOnStart</c>; the fallback here is defence in
    /// depth for a consumer's custom host that bypasses that pipeline — redaction was requested but no
    /// category resolved, so over-redact with the full set rather than silently emit unredacted PII.
    /// Matches the redactor's conservative-by-default posture (a false positive that masks text is
    /// acceptable; a leaked PAN is not). Kept in this one place rather than reimplemented by each
    /// caller, so the policy — and its warning message — can't drift between the two logging surfaces
    /// that need it.
    /// </remarks>
    public static ImmutableArray<RedactionCategory> Parse(IReadOnlyList<string>? names, ILogger logger, bool enabled)
    {
        var parsed = ParseNames(names, logger);
        if (!enabled || parsed.Length > 0)
        {
            return parsed;
        }

        logger.LogWarning(
            "Log redaction is enabled but no valid categories were configured; falling back " +
            "to the full redaction set ({CategoryCount} categories) to avoid emitting unredacted PII.",
            RedactionCategories.All.Length);
        return RedactionCategories.All;
    }

    private static ImmutableArray<RedactionCategory> ParseNames(IReadOnlyList<string>? names, ILogger logger)
    {
        if (names is null || names.Count == 0)
        {
            return [];
        }

        var parsed = new List<RedactionCategory>(names.Count);
        foreach (var name in names)
        {
            // Name-only, matching LogsConfigValidator exactly (the helper trims).
            if (EnumNameHelper.TryParseName<RedactionCategory>(name, out var category))
            {
                parsed.Add(category);
            }
            else
            {
                logger.LogWarning(
                    "Ignoring unknown log-redaction category '{Category}'. Valid values: {Valid}.",
                    name,
                    string.Join(", ", Enum.GetNames<RedactionCategory>()));
            }
        }

        return [.. parsed.Distinct()];
    }
}
