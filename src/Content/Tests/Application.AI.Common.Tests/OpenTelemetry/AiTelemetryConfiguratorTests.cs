using Application.AI.Common.OpenTelemetry;
using Application.Common.Interfaces.Telemetry;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.OpenTelemetry;

/// <summary>
/// Tests for <see cref="AiTelemetryConfigurator"/> covering interface implementation,
/// ordering, and method existence.
/// </summary>
public class AiTelemetryConfiguratorTests
{
    [Fact]
    public void Order_Returns150()
    {
        var configurator = new AiTelemetryConfigurator();

        configurator.Order.Should().Be(150);
    }

    [Fact]
    public void ImplementsITelemetryConfigurator()
    {
        var configurator = new AiTelemetryConfigurator();

        configurator.Should().BeAssignableTo<ITelemetryConfigurator>();
    }

    [Fact]
    public void Order_IsAfterBaseAppConfigurator_AndBeforeDomainSpecific()
    {
        var configurator = new AiTelemetryConfigurator();

        // Base app configurator is at 100, domain-specific at 200+
        configurator.Order.Should().BeGreaterThan(100);
        configurator.Order.Should().BeLessThan(200);
    }

    [Fact]
    public void ConfigureTracing_MethodExists()
    {
        var configurator = new AiTelemetryConfigurator();

        // Verify the method is available through the interface
        var method = typeof(ITelemetryConfigurator).GetMethod("ConfigureTracing");
        method.Should().NotBeNull();
    }

    [Fact]
    public void ConfigureMetrics_MethodExists()
    {
        var configurator = new AiTelemetryConfigurator();

        // Verify the method is available through the interface
        var method = typeof(ITelemetryConfigurator).GetMethod("ConfigureMetrics");
        method.Should().NotBeNull();
    }
}
