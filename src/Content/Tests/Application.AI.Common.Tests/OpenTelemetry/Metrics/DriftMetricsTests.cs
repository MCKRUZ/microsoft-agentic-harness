using Application.AI.Common.OpenTelemetry.Metrics;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.OpenTelemetry.Metrics;

public sealed class DriftMetricsTests
{
    [Fact]
    public void Evaluations_Counter_IsNotNull() =>
        DriftMetrics.Evaluations.Should().NotBeNull();

    [Fact]
    public void EscalationsTriggered_Counter_IsNotNull() =>
        DriftMetrics.EscalationsTriggered.Should().NotBeNull();

    [Fact]
    public void BaselinesUpdated_Counter_IsNotNull() =>
        DriftMetrics.BaselinesUpdated.Should().NotBeNull();

    [Fact]
    public void EvaluationDurationMs_Histogram_IsNotNull() =>
        DriftMetrics.EvaluationDurationMs.Should().NotBeNull();
}
