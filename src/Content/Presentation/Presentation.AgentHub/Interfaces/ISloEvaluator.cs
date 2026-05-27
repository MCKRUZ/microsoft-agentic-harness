using Application.Common.Models;

namespace Presentation.AgentHub.Interfaces;

/// <summary>
/// Evaluates all configured SLO targets against live Prometheus metrics and
/// returns the current verdict (Met / AtRisk / Breached) with error-budget status.
/// Injected into <see cref="Controllers.MetricsController"/> for the
/// <c>GET /api/metrics/slo</c> endpoint.
/// </summary>
public interface ISloEvaluator
{
    /// <summary>
    /// Evaluates every SLO target defined in <c>AppConfig:Observability:Slo:Targets</c>.
    /// Returns an empty list when SLO evaluation is disabled or no targets are configured.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// An immutable list of <see cref="SloStatus"/> records, one per configured target.
    /// Targets whose Prometheus query fails return <see cref="SloVerdict.Breached"/>
    /// with <c>CurrentValue = -1</c>.
    /// </returns>
    Task<IReadOnlyList<SloStatus>> EvaluateAllAsync(CancellationToken ct = default);
}
