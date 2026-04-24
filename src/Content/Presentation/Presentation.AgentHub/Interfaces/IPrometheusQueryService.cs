using Presentation.AgentHub.DTOs;

namespace Presentation.AgentHub.Interfaces;

/// <summary>
/// Abstraction for querying the Prometheus HTTP API. Implemented by
/// <see cref="Services.PrometheusQueryService"/>; enables unit testing
/// of <see cref="Controllers.MetricsController"/> without a live Prometheus instance.
/// </summary>
public interface IPrometheusQueryService
{
    /// <summary>
    /// Executes a PromQL instant query (<c>/api/v1/query</c>).
    /// </summary>
    /// <param name="query">The PromQL expression to evaluate.</param>
    /// <param name="time">Optional evaluation timestamp (RFC3339 or Unix). Defaults to server time.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Normalized query response with zero or more series.</returns>
    Task<MetricsQueryResponse> QueryInstantAsync(
        string query,
        string? time = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a PromQL range query (<c>/api/v1/query_range</c>).
    /// </summary>
    /// <param name="query">The PromQL expression to evaluate over the range.</param>
    /// <param name="start">Range start (RFC3339 or Unix timestamp).</param>
    /// <param name="end">Range end (RFC3339 or Unix timestamp).</param>
    /// <param name="step">Query resolution step (e.g. <c>"15s"</c>, <c>"1m"</c>, <c>"5m"</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Normalized query response with zero or more series.</returns>
    Task<MetricsQueryResponse> QueryRangeAsync(
        string query,
        string start,
        string end,
        string step,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether the Prometheus server is reachable and healthy.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Health status including server version if reachable.</returns>
    Task<PrometheusHealthResponse> GetHealthAsync(CancellationToken cancellationToken = default);
}
