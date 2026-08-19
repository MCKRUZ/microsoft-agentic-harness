using Application.AI.Common.Evaluation.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Application.AI.Common.Extensions;

/// <summary>
/// The one correct way to register an <see cref="IEvalMetric"/>: as a concrete singleton, as a
/// plain (non-keyed) <see cref="IEvalMetric"/>, and as a keyed <see cref="IEvalMetric"/> under the
/// same string the metric's own <see cref="IEvalMetric.Key"/> returns.
/// </summary>
/// <remarks>
/// <para>
/// <c>Infrastructure.AI.Evaluation.Runners.EvalRunner</c> (Infrastructure layer) builds its
/// metric lookup table exclusively from the non-keyed <c>IEnumerable&lt;IEvalMetric&gt;</c>
/// constructor parameter — a keyed-only registration is invisible to that enumerable and silently
/// resolves to <c>EvalRunner</c>'s "No registered metric with key '...'" branch, which reports a
/// non-gating <c>Verdict.Warn</c> rather than failing. This helper is the single place that
/// guarantees both registration shapes stay in sync (#436, following the exact gap #410 caused one
/// layer down at the parameter-key level).
/// </para>
/// <para>
/// Lives in the Application layer, not Infrastructure, because some <c>IEvalMetric</c>
/// implementations (e.g. the OWASP Agentic Top-10 metrics) are themselves registered from
/// <c>Application.AI.Common.DependencyInjection</c>, which cannot reference the Infrastructure
/// project that used to own the only copy of this pattern.
/// </para>
/// </remarks>
public static class EvalMetricRegistrationExtensions
{
    /// <summary>
    /// Registers <typeparamref name="TMetric"/> as a concrete singleton, a plain
    /// <see cref="IEvalMetric"/>, and a keyed <see cref="IEvalMetric"/> under <paramref name="key"/>.
    /// Calling this twice for the same <typeparamref name="TMetric"/> (e.g. a consumer that composes
    /// <c>AddEvaluationDependencies</c> more than once) is a no-op the second time, not a duplicate
    /// registration — <c>EvalRunner</c>'s constructor builds its lookup with <c>ToDictionary</c>,
    /// which throws on a duplicate key.
    /// </summary>
    /// <param name="key">
    /// Must equal the registered metric's own <see cref="IEvalMetric.Key"/> — the keyed alias exists
    /// only so a caller who already knows the key can resolve one metric without walking the whole
    /// <c>IEnumerable&lt;IEvalMetric&gt;</c>; the non-keyed registration is what actually makes the
    /// metric runnable. Enforced at first resolution: a mismatch throws
    /// <see cref="InvalidOperationException"/> rather than silently registering an alias under a
    /// string no <c>MetricSpec</c> will ever reference.
    /// </param>
    public static IServiceCollection AddEvalMetric<TMetric>(this IServiceCollection services, string key)
        where TMetric : class, IEvalMetric
    {
        if (services.Any(d => d.ServiceType == typeof(TMetric)))
            return services;

        services.AddSingleton<TMetric>();
        services.AddSingleton<IEvalMetric>(sp => sp.GetRequiredService<TMetric>());
        services.AddKeyedSingleton<IEvalMetric>(key, (sp, _) =>
        {
            var metric = sp.GetRequiredService<TMetric>();
            if (metric.Key != key)
            {
                throw new InvalidOperationException(
                    $"AddEvalMetric<{typeof(TMetric).Name}> was registered under key '{key}', but the " +
                    $"metric's own Key is '{metric.Key}'. The keyed alias must match IEvalMetric.Key exactly.");
            }
            return metric;
        });
        return services;
    }
}
