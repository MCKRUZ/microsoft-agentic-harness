using Application.AI.Common.Evaluation.Interfaces;
using Domain.Common.Config;
using FluentAssertions;
using Infrastructure.AI.Evaluation;
using Infrastructure.AI.Evaluation.Loaders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Presentation.Common.Extensions;
using Xunit;

namespace Presentation.EvalRunner.Tests.Composition;

/// <summary>
/// Validates every <c>eval-datasets/**/*.yaml</c> case's <c>MetricSpec.Parameters</c> keys against
/// the resolved metric's own declared <see cref="IEvalMetric.RecognizedParameterKeys"/> (#423).
/// </summary>
/// <remarks>
/// <see cref="Application.AI.Common.Evaluation.MetricSpecExtensions"/>'s accessors are deliberately
/// fail-soft — a typo'd parameter key is architecturally indistinguishable from an absent one at
/// score time, both fall back to the same default. That is exactly how #410's six eval cases
/// silently no-op'd or scored inverted for an unknown period, caught only when someone happened to
/// read the metric source by hand during an unrelated PR review — <c>Verdict.Warn</c> does
/// not gate CI (per its own doc comment), so nothing automated caught it. This test closes that gap
/// at build time: every dataset's declared parameters are checked against the metric's own declared
/// set before any case ever runs.
/// </remarks>
public sealed class MetricSpecParameterKeyValidationTests
{
    // The 10 OWASP metrics are registered keyed-only in Application.AI.Common's own
    // DependencyInjection.cs — see that file's remarks on EvalRunner building its metric map from
    // the non-keyed IEnumerable<IEvalMetric>, which keyed-only registrations are invisible to (a
    // separate, pre-existing gap, filed as #436 rather than folded into this fix). Resolved here by
    // key explicitly so this test's own coverage doesn't silently exclude them.
    private static readonly string[] KeyedOnlyMetricKeys =
    [
        "owasp.asi01.goal_hijack", "owasp.asi02.tool_misuse", "owasp.asi03.privilege_abuse",
        "owasp.asi04.supply_chain", "owasp.asi05.code_exec", "owasp.asi06.memory_poison",
        "owasp.asi07.inter_agent", "owasp.asi08.cascading", "owasp.asi09.human_trust",
        "owasp.asi10.rogue_agent"
    ];

    private static readonly Lazy<IReadOnlyDictionary<string, IEvalMetric>> MetricsByKey = new(BuildMetricsByKey);

    private static IReadOnlyDictionary<string, IEvalMetric> BuildMetricsByKey()
    {
        // Mirrors EvalRunnerValidateOnBuildTests' composition exactly — the full shared root plus
        // AddEvaluationDependencies is what actually constructs every metric (including the
        // LlmJudge/RAG ones with real constructor dependencies) against an empty in-memory config,
        // proven to build cleanly under ValidateOnBuild by that sibling test.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterConfigSections(configuration);
        var appConfig = configuration.GetSection("AppConfig").Get<AppConfig>() ?? new AppConfig();
        services.BuildGlobalSolutionServices(appConfig, includeHealthChecksUI: false);
        services.AddEvaluationDependencies();

        using var provider = services.BuildServiceProvider();

        var byKey = provider.GetServices<IEvalMetric>()
            .ToDictionary(m => m.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var key in KeyedOnlyMetricKeys)
            byKey[key] = provider.GetRequiredKeyedService<IEvalMetric>(key);

        return byKey;
    }

    public static IEnumerable<object[]> DatasetFiles()
    {
        var root = LocateEvalDatasetsDir();
        if (root is null) yield break;

        foreach (var f in Directory.EnumerateFiles(root, "*.yaml", SearchOption.AllDirectories))
            yield return [f];
    }

    [Fact]
    public void Eval_datasets_dir_is_locatable_and_has_expected_file_count()
    {
        // Same hard-fail guard SeedDatasetSmokeTests uses: without this, the Theory below silently
        // runs zero invocations on a host where path resolution breaks, hiding drift instead of
        // catching it.
        var root = LocateEvalDatasetsDir();
        root.Should().NotBeNull("'eval-datasets' must be reachable by walking up from the test bin dir");

        var count = Directory.EnumerateFiles(root!, "*.yaml", SearchOption.AllDirectories).Count();
        count.Should().BeGreaterThanOrEqualTo(9, "at least the 8 seed datasets plus owasp-agentic-top-10.yaml");
    }

    [Theory]
    [MemberData(nameof(DatasetFiles))]
    public async Task Every_case_parameter_key_is_recognized_by_its_metric(string path)
    {
        var metricsByKey = MetricsByKey.Value;
        var loader = new YamlEvalDatasetLoader();
        var dataset = await loader.LoadAsync(path, CancellationToken.None);

        var problems = new List<string>();
        foreach (var @case in dataset.Cases)
        {
            foreach (var spec in @case.MetricSpecs)
            {
                if (!metricsByKey.TryGetValue(spec.MetricKey, out var metric))
                {
                    problems.Add($"case '{@case.Id}': no registered metric with key '{spec.MetricKey}'.");
                    continue;
                }

                var unrecognized = spec.Parameters.Keys
                    .Where(k => !metric.RecognizedParameterKeys.Contains(k))
                    .ToList();
                if (unrecognized.Count > 0)
                {
                    problems.Add(
                        $"case '{@case.Id}', metric '{spec.MetricKey}': unrecognized parameter key(s) " +
                        $"{string.Join(", ", unrecognized.Select(k => $"'{k}'"))} " +
                        $"(recognized: {string.Join(", ", metric.RecognizedParameterKeys)})");
                }
            }
        }

        problems.Should().BeEmpty(
            $"{Path.GetFileName(path)} has case(s) whose MetricSpec.Parameters key the resolved " +
            "metric doesn't read — almost certainly a typo that would otherwise silently no-op " +
            "instead of failing the build (#423)");
    }

    private static string? LocateEvalDatasetsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "eval-datasets");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
