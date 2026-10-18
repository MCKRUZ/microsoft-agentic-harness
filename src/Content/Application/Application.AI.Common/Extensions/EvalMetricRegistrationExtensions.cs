using Application.AI.Common.Evaluation.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
    /// Calling this twice with the same type and key (e.g. a consumer that composes
    /// <c>AddEvaluationDependencies</c> more than once) is a no-op the second time, not a duplicate
    /// registration — <c>EvalRunner</c>'s constructor builds its lookup with <c>ToDictionary</c>,
    /// which throws on a duplicate key.
    /// </summary>
    /// <param name="key">
    /// Must equal the registered metric's own <see cref="IEvalMetric.Key"/> — the keyed alias exists
    /// only so a caller who already knows the key can resolve one metric without walking the whole
    /// <c>IEnumerable&lt;IEvalMetric&gt;</c>; the non-keyed registration is what actually makes the
    /// metric runnable. Enforced the first time either registration is resolved: a mismatch throws
    /// <see cref="InvalidOperationException"/> rather than silently registering an alias under a
    /// string no <c>MetricSpec</c> will ever reference. The check sits on the plain registration too
    /// because that is the one <c>EvalRunner</c> always resolves (#440); on the keyed alias alone it
    /// only ran when something asked for the alias by key, which no host does.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="TMetric"/> was already registered here under a different key, or another
    /// metric type already holds <paramref name="key"/>. Either would otherwise be dropped silently
    /// or crash <c>EvalRunner</c> later with a bare duplicate-key error.
    /// </exception>
    public static IServiceCollection AddEvalMetric<TMetric>(this IServiceCollection services, string key)
        where TMetric : class, IEvalMetric
    {
        foreach (var existing in services.Select(d => d.ImplementationInstance).OfType<EvalMetricRegistration>())
        {
            var sameType = existing.MetricType == typeof(TMetric);
            if (sameType && existing.Key == key)
                return services;

            if (sameType)
            {
                throw new InvalidOperationException(
                    $"AddEvalMetric<{typeof(TMetric).Name}> was already called with key '{existing.Key}' and is now " +
                    $"being registered under '{key}'. One metric type has one key; the second registration would " +
                    "otherwise be dropped silently.");
            }

            if (existing.Key == key)
            {
                throw new InvalidOperationException(
                    $"AddEvalMetric<{typeof(TMetric).Name}> cannot use key '{key}': it already belongs to " +
                    $"{existing.MetricType.Name}. EvalRunner builds its lookup by key and would fail on the duplicate.");
            }
        }

        services.AddSingleton(new EvalMetricRegistration(typeof(TMetric), key));

        // A consumer may have registered their own instance of TMetric first (it is the only way to give a
        // metric constructor arguments the container cannot supply). Keep it, and still add the two
        // IEvalMetric registrations that make it runnable; skipping them here was the original bug.
        services.TryAddSingleton<TMetric>();
        services.AddSingleton<IEvalMetric>(sp => ResolveChecked<TMetric>(sp, key));
        services.AddKeyedSingleton<IEvalMetric>(key, (sp, _) => ResolveChecked<TMetric>(sp, key));
        return services;
    }

    private static TMetric ResolveChecked<TMetric>(IServiceProvider sp, string key)
        where TMetric : class, IEvalMetric
    {
        var metric = sp.GetRequiredService<TMetric>();
        if (metric.Key != key)
        {
            throw new InvalidOperationException(
                $"AddEvalMetric<{typeof(TMetric).Name}> was registered under key '{key}', but the " +
                $"metric's own Key is '{metric.Key}'. The keyed alias must match IEvalMetric.Key exactly.");
        }
        return metric;
    }

    /// <summary>
    /// Records which metric type was registered under which key, so a second call can tell "the same
    /// registration again" from "a conflicting one". Registered as an instance so it is visible on the
    /// service collection without resolving anything.
    /// </summary>
    private sealed record EvalMetricRegistration(Type MetricType, string Key);
}
