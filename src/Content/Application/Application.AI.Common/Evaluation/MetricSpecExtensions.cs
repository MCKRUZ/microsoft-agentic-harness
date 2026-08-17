using Domain.AI.Evaluation;
using Microsoft.Extensions.Logging;

namespace Application.AI.Common.Evaluation;

/// <summary>
/// Shared fail-soft accessors for <see cref="MetricSpec.Parameters"/> — a free-form
/// string dictionary every <c>IEvalMetric</c> reads its case-author-supplied options from.
/// </summary>
/// <remarks>
/// One policy for the whole metric family: a missing or unparseable value never throws
/// out of <c>ScoreAsync</c>, it falls back to a caller-supplied default (optionally logged).
/// A bad case parameter must not take down an eval run.
/// </remarks>
public static class MetricSpecExtensions
{
    /// <summary>The raw string for <paramref name="key"/>, or <c>null</c> when absent or blank.</summary>
    public static string? GetString(this MetricSpec spec, string key)
        => spec.Parameters.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw) ? raw : null;

    /// <summary>
    /// The boolean value of <paramref name="key"/>, or <paramref name="defaultValue"/> when
    /// absent, blank, or unparseable. Logs a warning on the unparseable case only when
    /// <paramref name="logger"/> is supplied.
    /// </summary>
    public static bool GetBool(this MetricSpec spec, string key, bool defaultValue, ILogger? logger = null)
    {
        var raw = spec.GetString(key);
        if (raw is null)
        {
            return defaultValue;
        }
        if (bool.TryParse(raw, out var value))
        {
            return value;
        }
        logger?.LogWarning("Unparseable '{Key}' value '{Value}'; defaulting to {Default}.", key, raw, defaultValue);
        return defaultValue;
    }
}
