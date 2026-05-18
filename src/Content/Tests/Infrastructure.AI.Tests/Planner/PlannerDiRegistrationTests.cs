using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Attestation;
using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Interfaces.Sandbox;
using Docker.DotNet;
using Domain.AI.Planner;
using Domain.AI.Sandbox;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using FluentAssertions;
using Application.AI.Common.Models.Sandbox;
using Infrastructure.AI.Attestation;
using Infrastructure.AI.KnowledgeGraph;
using Infrastructure.AI.Persistence;
using Infrastructure.AI.Planner;
using Infrastructure.AI.Planner.StepExecutors;
using Infrastructure.AI.Sandbox;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Planner;

/// <summary>
/// DI registration tests for Phase 4 planner and sandbox services.
/// Verifies all services resolve from the container with correct types and lifetimes.
/// </summary>
public sealed class PlannerDiRegistrationTests : IDisposable
{
    private readonly ServiceProvider _provider;

    public PlannerDiRegistrationTests()
    {
        _provider = CreateServiceProvider();
    }

    public void Dispose() => _provider.Dispose();

    [Fact]
    public void DependencyInjection_AllPlannerServices_Resolvable()
    {
        using var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;

        sp.GetService<IPlanExecutor>().Should().NotBeNull().And.BeOfType<PlanExecutor>();
        sp.GetService<IPlanValidator>().Should().NotBeNull().And.BeOfType<PlanValidator>();
        sp.GetService<IPlanGenerator>().Should().NotBeNull().And.BeOfType<LlmPlanGeneratorService>();
        sp.GetService<IPlanStateStore>().Should().NotBeNull().And.BeOfType<EfCorePlanStateStore>();
    }

    [Fact]
    public void DependencyInjection_AllSandboxServices_Resolvable()
    {
        using var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;

        sp.GetService<IAttestationService>().Should().NotBeNull().And.BeOfType<HmacAttestationService>();
    }

    [Fact]
    public void DependencyInjection_KeyedStepExecutors_ResolveAllFiveTypes()
    {
        using var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;

        sp.GetRequiredKeyedService<IPlanStepExecutor>(StepType.LlmCall)
            .Should().BeOfType<LlmCallStepExecutor>();
        sp.GetRequiredKeyedService<IPlanStepExecutor>(StepType.ToolUse)
            .Should().BeOfType<ToolUseStepExecutor>();
        sp.GetRequiredKeyedService<IPlanStepExecutor>(StepType.HumanGate)
            .Should().BeOfType<HumanGateStepExecutor>();
        sp.GetRequiredKeyedService<IPlanStepExecutor>(StepType.ConditionalBranch)
            .Should().BeOfType<ConditionalBranchStepExecutor>();
        sp.GetRequiredKeyedService<IPlanStepExecutor>(StepType.SubPlanInvocation)
            .Should().BeOfType<SubPlanStepExecutor>();
    }

    [Fact]
    public void DependencyInjection_KeyedSandboxExecutors_ResolveBothTiers()
    {
        using var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;

        sp.GetRequiredKeyedService<ISandboxExecutor>(SandboxIsolationLevel.Process)
            .Should().BeOfType<ProcessSandboxExecutor>();
        sp.GetRequiredKeyedService<ISandboxExecutor>(SandboxIsolationLevel.Container)
            .Should().BeOfType<DockerSandboxExecutor>();
    }

    [Fact]
    public void DependencyInjection_PlannerDbContext_ScopedLifetime()
    {
        using var scope1 = _provider.CreateScope();
        using var scope2 = _provider.CreateScope();

        var ctx1 = scope1.ServiceProvider.GetRequiredService<PlannerDbContext>();
        var ctx2 = scope2.ServiceProvider.GetRequiredService<PlannerDbContext>();

        ctx1.Should().NotBeSameAs(ctx2);
    }

    [Fact]
    public async Task DependencyInjection_DbContextFactory_AvailableForSingletons()
    {
        var factory = _provider.GetRequiredService<IDbContextFactory<PlannerDbContext>>();

        await using var ctx = await factory.CreateDbContextAsync();

        ctx.Should().NotBeNull();
    }

    private static ServiceProvider CreateServiceProvider()
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig()
        };

        var services = new ServiceCollection();
        services.AddOptions();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IOptionsMonitor<AppConfig>>(new AppConfigMonitorStub(appConfig));
        services.AddSingleton<IOptionsMonitor<PlannerOptions>>(new PlannerOptionsMonitorStub(new PlannerOptions()));
        services.AddSingleton<IOptionsMonitor<SandboxOptions>>(
            new SandboxOptionsMonitorStub(new SandboxOptions()));
        services.AddSingleton<IOptionsMonitor<AttestationKeyOptions>>(
            new AttestationKeyOptionsMonitorStub(new AttestationKeyOptions
            {
                CurrentKeyVersion = "v1",
                HmacKeys = [new HmacKeyEntry { Version = "v1", Key = Convert.ToBase64String(new byte[32]) }]
            }));

        // External dependencies not registered by Infrastructure.AI
        services.AddSingleton<ISender>(new Mock<ISender>().Object);
        services.AddSingleton<IPlanProgressNotifier>(new Mock<IPlanProgressNotifier>().Object);
        services.AddSingleton<ICapabilityEnforcer>(new Mock<ICapabilityEnforcer>().Object);
        services.AddSingleton<IDockerClient>(new Mock<IDockerClient>().Object);

        // Knowledge graph (required by drift/learnings already in AddInfrastructureAIDependencies)
        services.AddKnowledgeGraphDependencies(appConfig);

        // Register all Infrastructure.AI services (now includes planner/sandbox)
        services.AddInfrastructureAIDependencies(appConfig);

        return services.BuildServiceProvider();
    }

    private sealed class AppConfigMonitorStub(AppConfig config) : IOptionsMonitor<AppConfig>
    {
        public AppConfig CurrentValue => config;
        public AppConfig Get(string? name) => config;
        public IDisposable? OnChange(Action<AppConfig, string?> listener) => null;
    }

    private sealed class PlannerOptionsMonitorStub(PlannerOptions config) : IOptionsMonitor<PlannerOptions>
    {
        public PlannerOptions CurrentValue => config;
        public PlannerOptions Get(string? name) => config;
        public IDisposable? OnChange(Action<PlannerOptions, string?> listener) => null;
    }

    private sealed class SandboxOptionsMonitorStub(SandboxOptions config)
        : IOptionsMonitor<SandboxOptions>
    {
        public SandboxOptions CurrentValue => config;
        public SandboxOptions Get(string? name) => config;
        public IDisposable? OnChange(Action<SandboxOptions, string?> listener) => null;
    }

    private sealed class AttestationKeyOptionsMonitorStub(AttestationKeyOptions config)
        : IOptionsMonitor<AttestationKeyOptions>
    {
        public AttestationKeyOptions CurrentValue => config;
        public AttestationKeyOptions Get(string? name) => config;
        public IDisposable? OnChange(Action<AttestationKeyOptions, string?> listener) => null;
    }
}
