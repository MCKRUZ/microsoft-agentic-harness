using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Attestation;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Planner;
using Application.AI.Common.Interfaces.Sandbox;
using Application.AI.Common.Prompts.Interfaces;
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
using Infrastructure.AI.Runs;
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
    public void DependencyInjection_RunSubstrate_HasADispatcherASweeperAndAResumeCheck()
    {
        // Registration is the whole feature for these three. Each is a background loop nothing else
        // calls, so an unregistered one is not a failure anyone sees — the dispatcher's absence leaves
        // every run sitting Queued, the sweeper's leaves the configured retention as a claim the host
        // never honours while finished runs accumulate for the life of the process, and the resume
        // check's leaves every approved human gate to wait out the parked-run ceiling and fail, days
        // after the approver said yes.
        var hosted = _provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .Select(service => service.GetType())
            .ToList();

        hosted.Should().Contain(typeof(RunDispatchBackgroundService));
        hosted.Should().Contain(typeof(RunRecordCleanupService));
        hosted.Should().Contain(typeof(ParkedRunResumeService));
    }

    [Fact]
    public void DependencyInjection_PlannerSchemaInitializer_RegisteredAndCreatesSchema()
    {
        // PR W1 regression guard: without this registration the first SavePlanAsync in any
        // host fails with "no such table" — the planner had no production schema init.
        _provider.GetService<SchemaInitializer<PlannerDbContext>>().Should().NotBeNull(
            "the planner must ensure its SQLite schema exists before the first store operation");
    }

    [Fact]
    public void DependencyInjection_PlanRunExecutor_RegisteredAsSingleton()
    {
        // W2 regression guard: the single arming site for enveloped plan runs must be resolvable
        // from the root provider (it creates its own per-run scope), mirroring IBundleRunExecutor.
        var first = _provider.GetService<IPlanRunExecutor>();
        var second = _provider.GetService<IPlanRunExecutor>();

        first.Should().NotBeNull().And.BeOfType<PlanRunExecutor>();
        second.Should().BeSameAs(first);
    }

    [Fact]
    public void DependencyInjection_NoToolIsRegisteredUnderAReservedPlanCapabilityName()
    {
        // PlanCapabilities names are matched out of the same AllowedTools string space as keyed ITool
        // registrations, so a tool registered under one of these keys would silently merge the two
        // grants. Production refuses the collision at boot (ReservedPlanCapabilityGuard); this pins
        // Infrastructure.AI's own registrations without booting a host.
        //
        // The keys must come off the ServiceDescriptors, NOT from GetServices<ITool>(): every tool
        // here is registered with AddKeyedSingleton, and the non-keyed GetServices overload does not
        // return keyed registrations — asserting over it can never fail.
        var offenders = CreateServices()
            .Where(d => d.ServiceType == typeof(Application.AI.Common.Interfaces.Tools.ITool)
                        && d.ServiceKey is string key
                        && PlanCapabilities.IsReserved(key))
            .Select(d => (string)d.ServiceKey!)
            .ToList();

        offenders.Should().BeEmpty(
            "a tool registered under a reserved plan-capability name would silently widen that grant");
    }

    [Fact]
    public void DependencyInjection_KeyedToolDescriptorsAreVisibleToTheReservedNameScan()
    {
        // Guards the guard: the reserved-name assertion above is only meaningful if this scan
        // actually sees the container's keyed ITool registrations. Its predecessor used
        // GetServices<ITool>(), which returns nothing for keyed registrations, so it passed
        // vacuously. If tool registration ever moves off keyed DI this fails and the reserved-name
        // test must be re-pointed rather than left silently inert.
        var keyedToolCount = CreateServices()
            .Count(d => d.ServiceType == typeof(Application.AI.Common.Interfaces.Tools.ITool)
                        && d.ServiceKey is string);

        keyedToolCount.Should().BeGreaterThan(0,
            "the reserved-name scan reads keyed ITool descriptors, so there must be some to read");
    }

    [Fact]
    public void DependencyInjection_AllSandboxServices_Resolvable()
    {
        using var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;

        sp.GetService<IAttestationService>().Should().NotBeNull().And.BeOfType<HmacAttestationService>();
    }

    [Fact]
    public void DependencyInjection_AttestationKeyOptionsValidator_WiredIntoOptionsPipeline()
    {
        // Regression guard for the "inert machinery" audit finding: the validator existed but
        // was never registered as IValidateOptions, so misconfigured key material was only
        // caught by the service's own runtime check instead of the options pipeline.
        _provider.GetService<IValidateOptions<AttestationKeyOptions>>()
            .Should().NotBeNull().And.BeOfType<AttestationKeyOptionsValidator>();
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

    private static ServiceProvider CreateServiceProvider() => CreateServices().BuildServiceProvider();

    /// <summary>
    /// Composes the same graph the provider is built from, returned un-built so descriptor-level
    /// assertions (registration keys, lifetimes) can inspect it without resolving anything.
    /// </summary>
    private static IServiceCollection CreateServices()
    {
        var appConfig = IsolatedAppConfig.Create();

        var services = new ServiceCollection();
        services.AddOptions();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IOptionsMonitor<AppConfig>>(new AppConfigMonitorStub(appConfig));
        services.AddSingleton<IOptionsMonitor<PlannerOptions>>(new PlannerOptionsMonitorStub(new PlannerOptions()));
        services.AddSingleton<IOptionsMonitor<SandboxExecutionOptions>>(
            new SandboxExecutionOptionsMonitorStub(new SandboxExecutionOptions()));
        services.AddSingleton<IOptionsMonitor<AttestationKeyOptions>>(
            new AttestationKeyOptionsMonitorStub(new AttestationKeyOptions
            {
                CurrentKeyVersion = "v1",
                HmacKeys = [new HmacKeyEntry { Version = "v1", Key = Convert.ToBase64String(new byte[32]) }]
            }));

        // External dependencies not registered by Infrastructure.AI
        // IAgentExecutionContext comes from Application.AI.Common's DI in production; the
        // knowledge-scope accessor (now consumed by EfCorePlanStateStore for plan ownership)
        // delegates agent identity to it.
        services.AddScoped(_ => new Mock<Application.AI.Common.Interfaces.Agent.IAgentExecutionContext>().Object);
        services.AddScoped(_ => new Mock<IToolInvocationGovernor>().Object);
        // Step executors take the observer chain as a required dependency, deliberately: an omitted
        // chain is indistinguishable at runtime from a host that registered no rules, so a nullable
        // default would let a composition silently run the plan path unguarded. The real composition
        // registers it in Application.AI.Common (see ToolCallObserverCompositionTests, which resolves
        // it from the actual root); this fixture hand-rolls its container, so it stubs it here.
        services.AddScoped(_ => new Mock<IToolCallObserverChain>().Object);
        services.AddSingleton(new Mock<Application.AI.Common.Interfaces.AI.IConversationBudgetTracker>().Object);
        services.AddSingleton<ISender>(new Mock<ISender>().Object);
        services.AddSingleton<IPlanProgressNotifier>(new Mock<IPlanProgressNotifier>().Object);
        services.AddSingleton<ICapabilityEnforcer>(new Mock<ICapabilityEnforcer>().Object);
        services.AddSingleton<ICompositeResponseSanitizer>(new Mock<ICompositeResponseSanitizer>().Object);
        services.AddSingleton<IDockerClient>(new Mock<IDockerClient>().Object);

        // Prompt registry services — registered by Presentation.Common.AddGlobalProjectDependencies
        // in production; supplied as mocks here since this DI test doesn't include that layer.
        services.AddSingleton<IPromptRegistry>(new Mock<IPromptRegistry>().Object);
        services.AddSingleton<IPromptRenderer>(new Mock<IPromptRenderer>().Object);
        services.AddSingleton<IPromptUsageRecorder>(new Mock<IPromptUsageRecorder>().Object);

        // Knowledge graph (required by drift/learnings already in AddInfrastructureAIDependencies)
        services.AddKnowledgeGraphDependencies(appConfig);

        // Register all Infrastructure.AI services (now includes planner/sandbox)
        services.AddInfrastructureAIDependencies(appConfig);

        return services;
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

    private sealed class SandboxExecutionOptionsMonitorStub(SandboxExecutionOptions config)
        : IOptionsMonitor<SandboxExecutionOptions>
    {
        public SandboxExecutionOptions CurrentValue => config;
        public SandboxExecutionOptions Get(string? name) => config;
        public IDisposable? OnChange(Action<SandboxExecutionOptions, string?> listener) => null;
    }

    private sealed class AttestationKeyOptionsMonitorStub(AttestationKeyOptions config)
        : IOptionsMonitor<AttestationKeyOptions>
    {
        public AttestationKeyOptions CurrentValue => config;
        public AttestationKeyOptions Get(string? name) => config;
        public IDisposable? OnChange(Action<AttestationKeyOptions, string?> listener) => null;
    }
}
