using Application.Common.Models;
using Domain.Common.Config;
using Domain.Common.Config.Observability;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Presentation.AgentHub.DTOs;
using Presentation.AgentHub.Interfaces;
using Presentation.AgentHub.Services;
using Xunit;

namespace Presentation.AgentHub.Tests.Services;

public sealed class SloEvaluationServiceTests
{
    private static SloEvaluationService CreateService(
        SloConfig sloConfig,
        Mock<IPrometheusQueryService>? prometheusMock = null)
    {
        var appConfig = new AppConfig
        {
            Observability = new ObservabilityConfig
            {
                Slo = sloConfig
            }
        };
        var configMonitor = Mock.Of<IOptionsMonitor<AppConfig>>(
            m => m.CurrentValue == appConfig);
        var prometheus = prometheusMock?.Object
            ?? Mock.Of<IPrometheusQueryService>();

        return new SloEvaluationService(
            configMonitor,
            prometheus,
            NullLogger<SloEvaluationService>.Instance);
    }

    private static SloTargetConfig CreateTarget(
        string id = "test-slo",
        string comparator = "lt",
        double target = 2000,
        double warningThreshold = 1500,
        string unit = "ms") =>
        new()
        {
            Id = id,
            Name = $"Test SLO {id}",
            Description = $"Test SLO description for {id}",
            ValueQuery = "test_metric_query",
            Unit = unit,
            Comparator = comparator,
            Target = target,
            WarningThreshold = warningThreshold,
            Window = "24h",
            ErrorBudgetPercent = 0.01
        };

    private static MetricsQueryResponse CreateSuccessResponse(string value) =>
        new()
        {
            Success = true,
            ResultType = "vector",
            Series =
            [
                new MetricSeries
                {
                    Labels = new Dictionary<string, string> { ["__name__"] = "test" },
                    DataPoints = [new MetricDataPoint { Timestamp = 1700000000, Value = value }]
                }
            ]
        };

    private static MetricsQueryResponse CreateEmptyResponse() =>
        new()
        {
            Success = true,
            ResultType = "vector",
            Series = []
        };

    private static MetricsQueryResponse CreateErrorResponse() =>
        new()
        {
            Success = false,
            Error = "connection refused"
        };

    // --- Disabled / Empty ---

    [Fact]
    public async Task EvaluateAll_WhenDisabled_ReturnsEmptyList()
    {
        var config = new SloConfig { Enabled = false, Targets = [CreateTarget()] };
        var service = CreateService(config);

        var result = await service.EvaluateAllAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task EvaluateAll_EmptyTargets_ReturnsEmptyList()
    {
        var config = new SloConfig { Enabled = true, Targets = [] };
        var service = CreateService(config);

        var result = await service.EvaluateAllAsync();

        result.Should().BeEmpty();
    }

    // --- Less-than comparator ---

    [Fact]
    public async Task EvaluateAll_WithLessThanComparator_Met_WhenBelowTarget()
    {
        // Target: < 2000, Warning: < 1500, Current: 1000 => Met (below both)
        var target = CreateTarget(comparator: "lt", target: 2000, warningThreshold: 1500);
        var config = new SloConfig { Enabled = true, Targets = [target] };
        var promMock = new Mock<IPrometheusQueryService>();
        promMock.Setup(p => p.QueryInstantAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSuccessResponse("1000"));
        var service = CreateService(config, promMock);

        var result = await service.EvaluateAllAsync();

        result.Should().HaveCount(1);
        result[0].Status.Should().Be(SloVerdict.Met);
        result[0].CurrentValue.Should().Be(1000);
        result[0].ErrorBudgetRemainingPercent.Should().Be(100.0);
    }

    [Fact]
    public async Task EvaluateAll_WithLessThanComparator_Breached_WhenAboveTarget()
    {
        // Target: < 2000, Current: 2500 => Breached (above target)
        var target = CreateTarget(comparator: "lt", target: 2000, warningThreshold: 1500);
        var config = new SloConfig { Enabled = true, Targets = [target] };
        var promMock = new Mock<IPrometheusQueryService>();
        promMock.Setup(p => p.QueryInstantAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSuccessResponse("2500"));
        var service = CreateService(config, promMock);

        var result = await service.EvaluateAllAsync();

        result.Should().HaveCount(1);
        result[0].Status.Should().Be(SloVerdict.Breached);
        result[0].CurrentValue.Should().Be(2500);
        result[0].ErrorBudgetRemainingPercent.Should().Be(0.0);
    }

    [Fact]
    public async Task EvaluateAll_WithLessThanComparator_AtRisk_WhenBetweenWarningAndTarget()
    {
        // Target: < 2000, Warning: < 1500, Current: 1750 => AtRisk (between warning and target)
        var target = CreateTarget(comparator: "lt", target: 2000, warningThreshold: 1500);
        var config = new SloConfig { Enabled = true, Targets = [target] };
        var promMock = new Mock<IPrometheusQueryService>();
        promMock.Setup(p => p.QueryInstantAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSuccessResponse("1750"));
        var service = CreateService(config, promMock);

        var result = await service.EvaluateAllAsync();

        result.Should().HaveCount(1);
        result[0].Status.Should().Be(SloVerdict.AtRisk);
        result[0].CurrentValue.Should().Be(1750);
        result[0].ErrorBudgetRemainingPercent.Should().BeGreaterThan(0.0);
        result[0].ErrorBudgetRemainingPercent.Should().BeLessThan(100.0);
    }

    // --- Greater-than comparator ---

    [Fact]
    public async Task EvaluateAll_WithGreaterThanComparator_Met_WhenAboveTarget()
    {
        // Target: > 99.0, Warning: > 99.5, Current: 99.9 => Met (above both)
        var target = CreateTarget(
            id: "uptime",
            comparator: "gt",
            target: 99.0,
            warningThreshold: 99.5,
            unit: "percent");
        var config = new SloConfig { Enabled = true, Targets = [target] };
        var promMock = new Mock<IPrometheusQueryService>();
        promMock.Setup(p => p.QueryInstantAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSuccessResponse("99.9"));
        var service = CreateService(config, promMock);

        var result = await service.EvaluateAllAsync();

        result.Should().HaveCount(1);
        result[0].Status.Should().Be(SloVerdict.Met);
        result[0].CurrentValue.Should().Be(99.9);
        result[0].ErrorBudgetRemainingPercent.Should().Be(100.0);
    }

    // --- Prometheus failures ---

    [Fact]
    public async Task EvaluateAll_WhenPrometheusDown_ReturnsBreachedStatus()
    {
        var target = CreateTarget();
        var config = new SloConfig { Enabled = true, Targets = [target] };
        var promMock = new Mock<IPrometheusQueryService>();
        promMock.Setup(p => p.QueryInstantAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));
        var service = CreateService(config, promMock);

        var result = await service.EvaluateAllAsync();

        result.Should().HaveCount(1);
        result[0].Status.Should().Be(SloVerdict.Breached);
        result[0].CurrentValue.Should().Be(-1);
        result[0].ErrorBudgetRemainingPercent.Should().Be(0.0);
    }

    [Fact]
    public async Task EvaluateAll_WhenPrometheusReturnsError_ReturnsBreachedStatus()
    {
        var target = CreateTarget();
        var config = new SloConfig { Enabled = true, Targets = [target] };
        var promMock = new Mock<IPrometheusQueryService>();
        promMock.Setup(p => p.QueryInstantAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateErrorResponse());
        var service = CreateService(config, promMock);

        var result = await service.EvaluateAllAsync();

        result.Should().HaveCount(1);
        result[0].Status.Should().Be(SloVerdict.Breached);
        result[0].CurrentValue.Should().Be(-1);
    }

    [Fact]
    public async Task EvaluateAll_WhenPrometheusReturnsEmptySeries_ReturnsBreachedStatus()
    {
        var target = CreateTarget();
        var config = new SloConfig { Enabled = true, Targets = [target] };
        var promMock = new Mock<IPrometheusQueryService>();
        promMock.Setup(p => p.QueryInstantAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateEmptyResponse());
        var service = CreateService(config, promMock);

        var result = await service.EvaluateAllAsync();

        result.Should().HaveCount(1);
        result[0].Status.Should().Be(SloVerdict.Breached);
        result[0].CurrentValue.Should().Be(-1);
    }

    // --- Output shape ---

    [Fact]
    public async Task EvaluateAll_PopulatesAllFieldsFromConfig()
    {
        var target = CreateTarget(id: "p95-latency", comparator: "lt", target: 2000, warningThreshold: 1500);
        var config = new SloConfig { Enabled = true, Targets = [target] };
        var promMock = new Mock<IPrometheusQueryService>();
        promMock.Setup(p => p.QueryInstantAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSuccessResponse("800"));
        var service = CreateService(config, promMock);

        var result = await service.EvaluateAllAsync();

        result.Should().HaveCount(1);
        var slo = result[0];
        slo.Id.Should().Be("p95-latency");
        slo.Name.Should().Be("Test SLO p95-latency");
        slo.Description.Should().NotBeNullOrEmpty();
        slo.Target.Should().Be(2000);
        slo.Unit.Should().Be("ms");
        slo.Comparator.Should().Be("lt");
        slo.SparklineQuery.Should().Be("test_metric_query");
    }

    // --- DeriveVerdict unit tests ---

    [Theory]
    [InlineData("lt", 500, 2000, 1500, SloVerdict.Met)]        // well below warning
    [InlineData("lt", 1600, 2000, 1500, SloVerdict.AtRisk)]    // between warning and target
    [InlineData("lt", 2500, 2000, 1500, SloVerdict.Breached)]  // above target
    [InlineData("gt", 99.9, 99.0, 99.5, SloVerdict.Met)]       // above both
    [InlineData("gt", 99.2, 99.0, 99.5, SloVerdict.AtRisk)]    // between target and warning
    [InlineData("gt", 98.5, 99.0, 99.5, SloVerdict.Breached)]  // below target
    [InlineData("lte", 2000, 2000, 1500, SloVerdict.AtRisk)]   // equal to target (meets lte)
    [InlineData("gte", 99.0, 99.0, 99.5, SloVerdict.AtRisk)]   // equal to target (meets gte)
    public void DeriveVerdict_ProducesCorrectVerdict(
        string comparator, double currentValue, double target, double warning, SloVerdict expected)
    {
        var config = new SloTargetConfig
        {
            Comparator = comparator,
            Target = target,
            WarningThreshold = warning
        };

        var verdict = SloEvaluationService.DeriveVerdict(config, currentValue);

        verdict.Should().Be(expected);
    }
}
