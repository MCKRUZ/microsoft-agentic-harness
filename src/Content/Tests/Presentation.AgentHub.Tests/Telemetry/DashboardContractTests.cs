using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using FluentAssertions.Execution;
using Presentation.AgentHub.Tests.Telemetry.Contracts;
using Xunit;

namespace Presentation.AgentHub.Tests.Telemetry;

/// <summary>
/// Validates that dashboard PromQL queries reference valid metric names that exist
/// in the naming contract. Catches drift between the instrumentation layer and the
/// dashboard catalog — a metric renamed in code but not in PromQL becomes a silent
/// dashboard panel with no data.
/// </summary>
[Trait("Category", "Contract")]
public sealed partial class DashboardContractTests
{
    private static readonly string CollectorConfigPath =
        Path.Combine(GetRepoRoot(), "scripts", "otel-collector", "config.yaml");

    private static readonly string Namespace =
        MetricNamingContract.GetCollectorNamespace(CollectorConfigPath);

    private static readonly IReadOnlySet<string> ContractNames =
        MetricNamingContract.GetAllExpectedPrometheusNamesSet(Namespace);

    [GeneratedRegex(@"(agentic_harness_[a-z][a-z0-9_]+)")]
    private static partial Regex MetricNamePattern();

    [Fact]
    public void AllDashboardMetrics_ExistInContract()
    {
        var catalogEntries = GetMetricCatalogEntries();
        var metricNamesInQueries = ExtractMetricNamesFromQueries(catalogEntries);

        using var scope = new AssertionScope();

        foreach (var metricName in metricNamesInQueries)
        {
            ContractNames.Should().Contain(metricName,
                $"dashboard query references '{metricName}' which must exist in the naming contract");
        }
    }

    [Fact]
    public void AllContractMetrics_HaveDashboardCoverage()
    {
        var catalogEntries = GetMetricCatalogEntries();
        var metricNamesInQueries = ExtractMetricNamesFromQueries(catalogEntries);

        // Soft assertion — we don't require 100% coverage but want visibility
        var coveredMetrics = ContractNames
            .Where(name => metricNamesInQueries.Contains(name))
            .ToHashSet();

        var uncoveredMetrics = ContractNames
            .Except(coveredMetrics)
            .OrderBy(n => n)
            .ToList();

        var coveragePercent = (double)coveredMetrics.Count / ContractNames.Count * 100;

        // At least 30% coverage — this is a soft target to catch egregious gaps
        coveragePercent.Should().BeGreaterThan(30,
            $"at least 30% of contract metrics should have dashboard panels. " +
            $"Uncovered ({uncoveredMetrics.Count}): {string.Join(", ", uncoveredMetrics.Take(10))}");
    }

    [Fact]
    public void DashboardQueries_UseCorrectNamespacePrefix()
    {
        var catalogEntries = GetMetricCatalogEntries();
        var metricNamesInQueries = ExtractMetricNamesFromQueries(catalogEntries);

        metricNamesInQueries.Should().NotBeEmpty(
            "the metric catalog must contain at least one PromQL query with metric references");

        metricNamesInQueries.Should().AllSatisfy(name =>
            name.Should().StartWith("agentic_harness_",
                $"all metric names in dashboard queries must use the 'agentic_harness_' namespace prefix"));
    }

    private static IReadOnlyList<object> GetMetricCatalogEntries()
    {
        // Access MetricCatalog via reflection since it's internal to Presentation.AgentHub
        var hubAssembly = Assembly.GetAssembly(typeof(Program))
            ?? throw new InvalidOperationException(
                "Could not load Presentation.AgentHub assembly via typeof(Program)");

        var catalogType = hubAssembly.GetTypes()
            .SingleOrDefault(t => t.Name == "MetricCatalog")
            ?? throw new InvalidOperationException(
                "MetricCatalog type not found in Presentation.AgentHub assembly");

        var entriesField = catalogType.GetField("Entries",
            BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "MetricCatalog.Entries static field not found");

        var entries = entriesField.GetValue(null) as System.Collections.IList
            ?? throw new InvalidOperationException(
                "MetricCatalog.Entries returned null or is not IList");

        return entries.Cast<object>().ToList();
    }

    private static HashSet<string> ExtractMetricNamesFromQueries(IReadOnlyList<object> entries)
    {
        var metricNames = new HashSet<string>();

        foreach (var entry in entries)
        {
            var queryProp = entry.GetType().GetProperty("Query")
                ?? throw new InvalidOperationException(
                    "MetricCatalogEntry does not have a Query property");

            var query = queryProp.GetValue(entry) as string;
            if (string.IsNullOrWhiteSpace(query)) continue;

            var matches = MetricNamePattern().Matches(query);
            foreach (Match match in matches)
            {
                metricNames.Add(match.Groups[1].Value);
            }
        }

        return metricNames;
    }

    private static string GetRepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null && !Directory.Exists(Path.Combine(dir, ".git")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new InvalidOperationException("Could not find repo root");
    }
}
