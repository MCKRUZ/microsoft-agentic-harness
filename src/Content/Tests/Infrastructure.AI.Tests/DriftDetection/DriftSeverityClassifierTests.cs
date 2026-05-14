using Domain.AI.DriftDetection;
using Domain.Common.Config.AI.DriftDetection;
using FluentAssertions;
using Infrastructure.AI.DriftDetection;
using Xunit;

namespace Infrastructure.AI.Tests.DriftDetection;

public sealed class DriftSeverityClassifierTests
{
    private static readonly DriftDetectionConfig DefaultConfig = new()
    {
        WarnThresholdSigma = 1.5,
        AlertThresholdSigma = 2.5,
        EscalateThresholdSigma = 3.0
    };

    [Fact]
    public void Classify_BelowWarn_ReturnsNone()
    {
        var severity = DriftSeverityClassifier.Classify(1.0, DefaultConfig);
        severity.Should().Be(DriftSeverity.None);
    }

    [Fact]
    public void Classify_BetweenWarnAndAlert_ReturnsWarn()
    {
        var severity = DriftSeverityClassifier.Classify(2.0, DefaultConfig);
        severity.Should().Be(DriftSeverity.Warn);
    }

    [Fact]
    public void Classify_BetweenAlertAndEscalate_ReturnsAlert()
    {
        var severity = DriftSeverityClassifier.Classify(2.8, DefaultConfig);
        severity.Should().Be(DriftSeverity.Alert);
    }

    [Fact]
    public void Classify_AboveEscalate_ReturnsEscalate()
    {
        var severity = DriftSeverityClassifier.Classify(3.5, DefaultConfig);
        severity.Should().Be(DriftSeverity.Escalate);
    }

    [Theory]
    [InlineData(1.5, DriftSeverity.Warn)]
    [InlineData(2.5, DriftSeverity.Alert)]
    [InlineData(3.0, DriftSeverity.Escalate)]
    public void Classify_ExactlyAtThreshold_ReturnsHigherSeverity(double deviation, DriftSeverity expected)
    {
        var severity = DriftSeverityClassifier.Classify(deviation, DefaultConfig);
        severity.Should().Be(expected);
    }
}
