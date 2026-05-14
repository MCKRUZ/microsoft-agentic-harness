using System.Text.Json;
using Microsoft.Extensions.Options;
using Presentation.AgentHub.Config;
using Presentation.AgentHub.DTOs;
using Presentation.AgentHub.Interfaces;

namespace Presentation.AgentHub.Services;

/// <summary>
/// Typed <see cref="HttpClient"/> wrapper that queries the Prometheus HTTP API.
/// Registered via <c>AddHttpClient&lt;IPrometheusQueryService, PrometheusQueryService&gt;</c>
/// in <see cref="DependencyInjection"/>.
/// </summary>
public sealed class PrometheusQueryService : IPrometheusQueryService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<PrometheusQueryService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Initialises the service with a configured <see cref="HttpClient"/>.</summary>
    public PrometheusQueryService(
        HttpClient httpClient,
        ILogger<PrometheusQueryService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<MetricsQueryResponse> QueryInstantAsync(
        string query,
        string? time = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, string> { ["query"] = query };
        if (!string.IsNullOrWhiteSpace(time))
            parameters["time"] = time;

        var url = $"api/v1/query?{await ToQueryStringAsync(parameters)}";
        return await ExecuteQueryAsync(url, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<MetricsQueryResponse> QueryRangeAsync(
        string query,
        string start,
        string end,
        string step,
        CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, string>
        {
            ["query"] = query,
            ["start"] = start,
            ["end"] = end,
            ["step"] = step,
        };

        var url = $"api/v1/query_range?{await ToQueryStringAsync(parameters)}";
        return await ExecuteQueryAsync(url, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PrometheusHealthResponse> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("api/v1/status/buildinfo", cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var version = doc.RootElement
                .GetProperty("data")
                .GetProperty("version")
                .GetString();

            return new PrometheusHealthResponse { Healthy = true, Version = version };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Prometheus health check failed");
            return new PrometheusHealthResponse { Healthy = false, Error = ex.Message };
        }
    }

    private async Task<MetricsQueryResponse> ExecuteQueryAsync(
        string url,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.GetAsync(url, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var apiResponse = JsonSerializer.Deserialize<PrometheusApiResponse>(json, JsonOptions);

            if (apiResponse?.Status != "success")
            {
                _logger.LogDebug("Prometheus returned non-success for {Url}: {Status}", url, apiResponse?.Status);
                return new MetricsQueryResponse
                {
                    Success = false,
                    ResultType = "matrix",
                    Series = [],
                    Error = apiResponse?.Error ?? apiResponse?.ErrorType ?? "Prometheus returned non-success status",
                };
            }

            var series = (apiResponse.Data?.Result ?? [])
                .Select(NormalizeSeries)
                .ToList();

            return new MetricsQueryResponse
            {
                Success = true,
                ResultType = apiResponse.Data?.ResultType ?? "unknown",
                Series = series,
            };
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Prometheus is unreachable — returning empty series: {Url}", url);
            return new MetricsQueryResponse
            {
                Success = true,
                ResultType = "matrix",
                Series = [],
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Prometheus query failed: {Url}", url);
            return new MetricsQueryResponse { Success = false, Error = ex.Message };
        }
    }

    private static MetricSeries NormalizeSeries(PrometheusSeriesResult result)
    {
        var dataPoints = new List<MetricDataPoint>();

        if (result.Values is { Count: > 0 })
        {
            foreach (var pair in result.Values)
            {
                if (pair.Count >= 2)
                    dataPoints.Add(ParseDataPoint(pair[0], pair[1]));
            }
        }
        else if (result.Value is { Count: >= 2 })
        {
            dataPoints.Add(ParseDataPoint(result.Value[0], result.Value[1]));
        }

        return new MetricSeries
        {
            Labels = result.Metric,
            DataPoints = dataPoints,
        };
    }

    private static MetricDataPoint ParseDataPoint(object timestamp, object value)
    {
        var ts = timestamp switch
        {
            JsonElement je when je.ValueKind == JsonValueKind.Number => je.GetDouble(),
            double d => d,
            _ => 0.0,
        };

        var val = value switch
        {
            JsonElement je => je.GetString() ?? je.ToString(),
            string s => s,
            _ => value.ToString() ?? string.Empty,
        };

        return new MetricDataPoint { Timestamp = ts, Value = val };
    }

    private static Task<string> ToQueryStringAsync(Dictionary<string, string> parameters)
    {
        using var content = new FormUrlEncodedContent(parameters);
        return content.ReadAsStringAsync();
    }
}
