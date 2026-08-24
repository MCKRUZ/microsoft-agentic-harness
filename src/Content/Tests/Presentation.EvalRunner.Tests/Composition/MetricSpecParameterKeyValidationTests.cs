using Application.AI.Common.Evaluation.Interfaces;
using FluentAssertions;
using Infrastructure.AI.Evaluation.Loaders;
using Microsoft.Extensions.DependencyInjection;
using Tests.Common;
using Xunit;

namespace Presentation.EvalRunner.Tests.Composition;

/// <summary>
/// Validates every <c>eval-datasets/**/*.yaml</c> case's <c>MetricSpec.Parameters</c> keys against
/// the resolved metric's own declared <see cref="IEvalMetric.RecognizedParameterKeys"/> (#423), and
/// each case's <c>InvocationOverrides</c> keys against the resolved invoker's own declared
/// <see cref="IAgentInvoker.RecognizedOverrideKeys"/> (#437).
/// </summary>
/// <remarks>
/// <see cref="Application.AI.Common.Evaluation.MetricSpecExtensions"/>'s accessors are deliberately
/// fail-soft — a typo'd parameter key is architecturally indistinguishable from an absent one at
/// score time, both fall back to the same default. That is exactly how #410's six eval cases
/// silently no-op'd or scored inverted for an unknown period, caught only when someone happened to
/// read the metric source by hand during an unrelated PR review — <c>Verdict.Warn</c> does
/// not gate CI (per its own doc comment), so nothing automated caught it. This test closes that gap
/// at build time — for the repo's own checked-in datasets only; <c>EvalRunner</c>'s own
/// <c>ValidateRecognizedKeys</c> closes the same gap at runtime for any dataset a consumer actually
/// runs (#437) — checking every dataset's declared keys against the resolved metric's/invoker's own
/// declared set before any case ever runs.
/// </remarks>
public sealed class MetricSpecParameterKeyValidationTests
{
    private static readonly Lazy<IReadOnlyDictionary<string, IEvalMetric>> MetricsByKey = new(BuildMetricsByKey);
    private static readonly Lazy<IAgentInvoker> Invoker = new(BuildInvoker);

    private static IReadOnlyDictionary<string, IEvalMetric> BuildMetricsByKey()
    {
        // EvalRunnerTestComposition mirrors the real EvalRunner host exactly — the full shared root
        // plus AddEvaluationDependencies is what actually constructs every metric (including the
        // LlmJudge/RAG ones with real constructor dependencies) against an empty in-memory config,
        // proven to build cleanly under ValidateOnBuild by the sibling EvalRunnerValidateOnBuildTests.
        // Every registered IEvalMetric — including the 10 OWASP metrics, previously keyed-only and
        // invisible here until #436 — resolves through this single non-keyed enumeration; that is
        // exactly the guarantee EvalRunnerMetricDiscoveryTests pins directly.
        var services = EvalRunnerTestComposition.BuildServices();
        using var provider = services.BuildServiceProvider();

        return provider.GetServices<IEvalMetric>()
            .ToDictionary(m => m.Key, StringComparer.OrdinalIgnoreCase);
    }

    private static IAgentInvoker BuildInvoker()
    {
        // The real registered IAgentInvoker (RouterEvalInvoker wrapping HarnessAgentInvoker) — not a
        // fake — so RecognizedOverrideKeys reflects the actual union every production run checks
        // InvocationOverrides against (#437).
        var services = EvalRunnerTestComposition.BuildServices();
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IAgentInvoker>();
    }

    public static IEnumerable<object[]> DatasetFiles()
    {
        var root = RepoRoot.Combine("eval-datasets");
        foreach (var f in Directory.EnumerateFiles(root, "*.yaml", SearchOption.AllDirectories))
            yield return [f];
    }

    [Fact]
    public void Eval_datasets_dir_has_expected_file_count()
    {
        // Same hard-fail guard SeedDatasetSmokeTests uses: without this, the Theory below silently
        // runs zero invocations if the datasets are ever deleted, hiding drift instead of catching
        // it. RepoRoot.Combine itself throws (with a clear message) if the repo root can't be
        // located at all, so there's no separate "is locatable" check needed here.
        var count = Directory.EnumerateFiles(
            RepoRoot.Combine("eval-datasets"), "*.yaml", SearchOption.AllDirectories).Count();
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

    /// <summary>
    /// #437: the same check as <see cref="Every_case_parameter_key_is_recognized_by_its_metric"/>,
    /// generalized to <see cref="Domain.AI.Evaluation.EvalCase.InvocationOverrides"/> — the same
    /// free-form, hand-authored, fail-soft-accessed key/value bag <c>MetricSpec.Parameters</c> is,
    /// parsed from the same YAML files, carrying the identical typo risk.
    /// </summary>
    [Theory]
    [MemberData(nameof(DatasetFiles))]
    public async Task Every_case_invocation_override_key_is_recognized_by_the_invoker(string path)
    {
        var invoker = Invoker.Value;
        var loader = new YamlEvalDatasetLoader();
        var dataset = await loader.LoadAsync(path, CancellationToken.None);

        var problems = new List<string>();
        foreach (var @case in dataset.Cases)
        {
            var unrecognized = @case.InvocationOverrides.Keys
                .Where(k => !invoker.RecognizedOverrideKeys.Contains(k))
                .ToList();
            if (unrecognized.Count > 0)
            {
                problems.Add(
                    $"case '{@case.Id}': unrecognized invocation override key(s) " +
                    $"{string.Join(", ", unrecognized.Select(k => $"'{k}'"))} " +
                    $"(recognized: {string.Join(", ", invoker.RecognizedOverrideKeys)})");
            }
        }

        problems.Should().BeEmpty(
            $"{Path.GetFileName(path)} has case(s) whose InvocationOverrides key the resolved " +
            "invoker doesn't read — almost certainly a typo that would otherwise silently no-op " +
            "instead of failing the build (#437)");
    }
}
