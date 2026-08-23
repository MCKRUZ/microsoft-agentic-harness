using System.Collections.Immutable;
using Domain.AI.Telemetry.Redaction;
using Domain.Common.Helpers;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Observability.Processors;

/// <summary>
/// Parses <c>LogsConfig.RedactionCategories</c>' configured names to <see cref="RedactionCategory"/>
/// values — shared by <see cref="LogRecordRedactionProcessor"/> and the local-sink redactor (#457) so
/// the two logging surfaces this one config section drives read it identically.
/// </summary>
internal static class RedactionCategoryParser
{
    /// <summary>
    /// Parses the configured category names, skipping (and logging) any name the enum does not
    /// recognise. Startup validation (<c>LogsConfigValidator</c>) already rejects unknown names on
    /// every host that wires <c>ValidateOnStart</c>; this is defence in depth for one that bypasses it.
    /// </summary>
    public static ImmutableArray<RedactionCategory> Parse(IReadOnlyList<string>? names, ILogger logger)
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
