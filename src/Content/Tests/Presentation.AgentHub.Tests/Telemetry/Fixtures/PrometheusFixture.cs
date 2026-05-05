using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Xunit;

namespace Presentation.AgentHub.Tests.Telemetry.Fixtures;

/// <summary>
/// Testcontainers-based fixture that spins up an OTel Collector and Prometheus instance
/// on a shared Docker network. The collector receives OTLP from the app under test,
/// exports to Prometheus, and Prometheus scrapes the collector's metrics endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Requires Docker to be running on the host. Tests using this fixture should be
/// decorated with <c>[Trait("Category", "E2E")]</c> so they can be filtered out
/// in environments without Docker (e.g., CI without DinD).
/// </para>
/// <para>
/// The collector mounts the project's <c>scripts/otel-collector/config.yaml</c>
/// to ensure E2E tests exercise the real collector configuration, including the
/// <c>filter/app_only</c> processor that rejects traffic not from
/// <c>Presentation.AgentHub</c>.
/// </para>
/// </remarks>
public sealed class PrometheusFixture : IAsyncLifetime
{
    private INetwork _network = null!;
    private IContainer _collector = null!;
    private IContainer _prometheus = null!;

    /// <summary>OTLP gRPC endpoint exposed by the collector container on the host.</summary>
    public string CollectorOtlpGrpcEndpoint { get; private set; } = null!;

    /// <summary>OTLP HTTP endpoint exposed by the collector container on the host.</summary>
    public string CollectorOtlpHttpEndpoint { get; private set; } = null!;

    /// <summary>Prometheus HTTP API endpoint on the host (e.g., http://localhost:{port}).</summary>
    public string PrometheusQueryEndpoint { get; private set; } = null!;

    private static readonly string RepoRoot = GetRepoRoot();

    /// <inheritdoc/>
    public async Task InitializeAsync()
    {
        _network = new NetworkBuilder()
            .WithName($"telemetry-e2e-{Guid.NewGuid():N}")
            .Build();

        await _network.CreateAsync();

        _collector = new ContainerBuilder()
            .WithImage("otel/opentelemetry-collector-contrib:0.123.0")
            .WithName($"collector-{Guid.NewGuid():N}")
            .WithNetwork(_network)
            .WithNetworkAliases("otel-collector")
            .WithPortBinding(4317, true)
            .WithPortBinding(4318, true)
            .WithPortBinding(8889, true)
            .WithResourceMapping(
                Path.Combine(RepoRoot, "scripts", "otel-collector", "config.yaml"),
                "/etc/otelcol-contrib/config.yaml")
            .WithEnvironment("DEPLOYMENT_ENVIRONMENT", "test")
            .WithEnvironment("TEMPO_ENDPOINT", "localhost:4317")
            .WithEnvironment("APPLICATIONINSIGHTS_CONNECTION_STRING", "InstrumentationKey=00000000-0000-0000-0000-000000000000")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilPortIsAvailable(4317)
                .UntilPortIsAvailable(8889))
            .Build();

        await _collector.StartAsync();

        var promConfig = """
            global:
              scrape_interval: 2s
              evaluation_interval: 2s
            scrape_configs:
              - job_name: 'otel-collector'
                static_configs:
                  - targets: ['otel-collector:8889']
            """;

        var promConfigPath = Path.Combine(Path.GetTempPath(), $"prom-{Guid.NewGuid():N}.yml");
        await File.WriteAllTextAsync(promConfigPath, promConfig);

        _prometheus = new ContainerBuilder()
            .WithImage("prom/prometheus:v2.53.0")
            .WithName($"prometheus-{Guid.NewGuid():N}")
            .WithNetwork(_network)
            .WithPortBinding(9090, true)
            .WithResourceMapping(promConfigPath, "/etc/prometheus/prometheus.yml")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilPortIsAvailable(9090))
            .Build();

        await _prometheus.StartAsync();

        CollectorOtlpGrpcEndpoint = $"http://localhost:{_collector.GetMappedPublicPort(4317)}";
        CollectorOtlpHttpEndpoint = $"http://localhost:{_collector.GetMappedPublicPort(4318)}";
        PrometheusQueryEndpoint = $"http://localhost:{_prometheus.GetMappedPublicPort(9090)}";
    }

    /// <summary>
    /// Queries the Prometheus HTTP API with the given PromQL expression
    /// and returns the <c>data.result</c> array, or <c>null</c> if empty.
    /// </summary>
    public async Task<JsonElement?> QueryPrometheus(string promql)
    {
        using var client = new HttpClient();
        var url = $"{PrometheusQueryEndpoint}/api/v1/query?query={Uri.EscapeDataString(promql)}";
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var result = json.GetProperty("data").GetProperty("result");
        return result.GetArrayLength() > 0 ? result : null;
    }

    /// <summary>
    /// Polls Prometheus until the given PromQL expression returns at least one result,
    /// or until the timeout expires. Returns <c>true</c> if the metric appeared.
    /// </summary>
    public async Task<bool> WaitForMetric(string promql, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var result = await QueryPrometheus(promql);
                if (result.HasValue)
                    return true;
            }
            catch
            {
                // Container may not be ready yet — retry.
            }

            await Task.Delay(2000);
        }

        return false;
    }

    /// <inheritdoc/>
    public async Task DisposeAsync()
    {
        if (_prometheus != null) await _prometheus.DisposeAsync();
        if (_collector != null) await _collector.DisposeAsync();
        if (_network != null) await _network.DeleteAsync();
    }

    private static string GetRepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null && !Directory.Exists(Path.Combine(dir, ".git")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new InvalidOperationException("Could not find repo root (.git directory)");
    }
}
