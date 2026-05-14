using Application.AI.Common.OpenTelemetry.Metrics;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.CQRS.Learnings;

/// <summary>
/// Smoke tests for <see cref="LearningsMetrics"/> instrument creation.
/// Verifies static instruments are non-null and have correct names.
/// </summary>
public sealed class LearningsMetricsTests
{
    [Fact]
    public void Remembered_Counter_IsNotNull()
    {
        LearningsMetrics.Remembered.Should().NotBeNull();
        LearningsMetrics.Remembered.Name.Should().Be("agent.learning.remembered");
    }

    [Fact]
    public void Recalled_Counter_IsNotNull()
    {
        LearningsMetrics.Recalled.Should().NotBeNull();
        LearningsMetrics.Recalled.Name.Should().Be("agent.learning.recalled");
    }

    [Fact]
    public void Forgotten_Counter_IsNotNull()
    {
        LearningsMetrics.Forgotten.Should().NotBeNull();
        LearningsMetrics.Forgotten.Name.Should().Be("agent.learning.forgotten");
    }

    [Fact]
    public void Improved_Counter_IsNotNull()
    {
        LearningsMetrics.Improved.Should().NotBeNull();
        LearningsMetrics.Improved.Name.Should().Be("agent.learning.improved");
    }

    [Fact]
    public void Pruned_Counter_IsNotNull()
    {
        LearningsMetrics.Pruned.Should().NotBeNull();
        LearningsMetrics.Pruned.Name.Should().Be("agent.learning.pruned");
    }

    [Fact]
    public void RecallDurationMs_Histogram_IsNotNull()
    {
        LearningsMetrics.RecallDurationMs.Should().NotBeNull();
        LearningsMetrics.RecallDurationMs.Name.Should().Be("agent.learning.recall_duration_ms");
    }
}
