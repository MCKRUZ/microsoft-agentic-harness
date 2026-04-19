using Application.AI.Common;
using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Agent;
using Application.AI.Common.Services.Context;
using Application.AI.Common.Services.Tools;
using Application.Common.Interfaces.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Application.AI.Common.Tests;

/// <summary>
/// Tests for <see cref="DependencyInjection.AddApplicationAIDependencies"/> verifying
/// that all expected services are registered with correct lifetimes.
/// </summary>
public class DependencyInjectionTests
{
    private static IServiceCollection CreateServicesWithAIDependencies()
    {
        var services = new ServiceCollection();
        services.AddApplicationAIDependencies();
        return services;
    }

    [Fact]
    public void AddApplicationAIDependencies_RegistersAgentExecutionContext_AsScoped()
    {
        var services = CreateServicesWithAIDependencies();

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IAgentExecutionContext));
        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Scoped);
        descriptor.ImplementationType.Should().Be(typeof(AgentExecutionContext));
    }

    [Fact]
    public void AddApplicationAIDependencies_RegistersToolConverter_AsSingleton()
    {
        var services = CreateServicesWithAIDependencies();

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IToolConverter));
        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
        descriptor.ImplementationType.Should().Be(typeof(AIToolConverter));
    }

    [Fact]
    public void AddApplicationAIDependencies_RegistersContextBudgetTracker_AsSingleton()
    {
        var services = CreateServicesWithAIDependencies();

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IContextBudgetTracker));
        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
        descriptor.ImplementationType.Should().Be(typeof(ContextBudgetTracker));
    }

    [Fact]
    public void AddApplicationAIDependencies_RegistersTieredContextAssembler_AsTransient()
    {
        var services = CreateServicesWithAIDependencies();

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ITieredContextAssembler));
        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Transient);
        descriptor.ImplementationType.Should().Be(typeof(TieredContextAssembler));
    }

    [Fact]
    public void AddApplicationAIDependencies_RegistersAgentExecutionContextFactory_AsSingleton()
    {
        var services = CreateServicesWithAIDependencies();

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(AgentExecutionContextFactory));
        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddApplicationAIDependencies_RegistersAiTelemetryConfigurator()
    {
        var services = CreateServicesWithAIDependencies();

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ITelemetryConfigurator));
        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddApplicationAIDependencies_RegistersSixPipelineBehaviors()
    {
        var services = CreateServicesWithAIDependencies();

        var behaviors = services
            .Where(d => d.ServiceType.IsGenericType &&
                        d.ServiceType.GetGenericTypeDefinition() == typeof(MediatR.IPipelineBehavior<,>))
            .ToList();

        behaviors.Should().HaveCount(6);
        behaviors.Should().OnlyContain(d => d.Lifetime == ServiceLifetime.Transient);
    }

    [Fact]
    public void AddApplicationAIDependencies_ReturnsServiceCollection_ForChaining()
    {
        var services = new ServiceCollection();
        var returned = services.AddApplicationAIDependencies();

        returned.Should().BeSameAs(services);
    }
}
