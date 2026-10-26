using Application.AI.Common.Interfaces.Telemetry;
using Application.AI.Common.Services.Telemetry;
using Application.Common.Interfaces.Common;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Infrastructure.Observability.Tests;

public sealed class DependencyInjectionTests
{
    [Fact]
    public void AddInfrastructureObservabilityDependencies_RegistersIOwnerOnlyDirectoryCreator_OnItsOwn()
    {
        // Found by code review on #738: Agent365StartupValidator takes IOwnerOnlyDirectoryCreator, but
        // the only registration for it lived in Infrastructure.AI's own DI module — a project this one
        // did not reference. Production only worked because the real composition root happens to wire
        // both modules together; nothing made that an enforced fact rather than a coincidence, which is
        // exactly the shape #671/#672/#673 already found and fixed for a different consumer of this same
        // seam (see Infrastructure.AI.Tests.DependencyInjectionTests). This test proves
        // AddInfrastructureObservabilityDependencies is now self-sufficient: called ALONE, with no other
        // module wired alongside it, IOwnerOnlyDirectoryCreator still resolves.
        var services = new ServiceCollection();

        services.AddInfrastructureObservabilityDependencies();
        using var provider = services.BuildServiceProvider();

        var creator = provider.GetService<IOwnerOnlyDirectoryCreator>();

        creator.Should().NotBeNull();
    }

    [Fact]
    public void AddInfrastructureObservabilityDependencies_RegistersTheTelemetryPseudonymizerAsOneSingleton_OnItsOwn()
    {
        // The configurator hands this to the PII filter and the tool-argument fingerprint (#903). It must be one
        // singleton: with no key configured each instance makes its own random key, so two instances would hash the
        // same person differently in the same process.
        var services = new ServiceCollection();

        services.AddInfrastructureObservabilityDependencies();
        services.AddInfrastructureObservabilityDependencies();

        services.Should().ContainSingle(d => d.ServiceType == typeof(ITelemetryPseudonymizer))
            .Which.Should().Match<ServiceDescriptor>(d =>
                d.Lifetime == ServiceLifetime.Singleton && d.ImplementationType == typeof(HmacTelemetryPseudonymizer));
    }
}
