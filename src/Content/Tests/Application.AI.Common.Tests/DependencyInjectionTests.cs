using Application.AI.Common;
using Application.AI.Common.Evaluation.Interfaces;
using Application.AI.Common.Factories;
using Application.AI.Common.Interfaces.AI;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Context;
using Application.AI.Common.Interfaces.Governance;
using Application.AI.Common.Interfaces.Skills;
using Application.AI.Common.Interfaces.Tools;
using Application.AI.Common.Services.Agent;
using Application.AI.Common.Services.Context;
using Application.AI.Common.Services.Governance;
using Application.AI.Common.Services.Skills;
using Application.AI.Common.Services.AI;
using Application.AI.Common.Services.Tools;
using Application.Common.Interfaces.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
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

    /// <summary>
    /// The shared telemetry recorder must be registered here, in the assembly that owns it.
    /// </summary>
    /// <remarks>
    /// Its own unit tests construct it directly, so they stay green with this registration deleted —
    /// and three transports take it as a constructor dependency, so a host missing it fails to build its
    /// conversation handler at all. Same trap as issue #279: a registration is untested unless something
    /// resolves it from the real composition method.
    /// </remarks>
    [Fact]
    public void AddApplicationAIDependencies_RegistersTheSharedTelemetryRecorder()
    {
        var services = CreateServicesWithAIDependencies();

        var descriptor = services.FirstOrDefault(
            d => d.ServiceType == typeof(IConversationTelemetryRecorder));

        descriptor.Should().NotBeNull(
            "three transports take this as a constructor dependency; unregistered, none of them can be built");
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
        descriptor.ImplementationType.Should().Be<ConversationTelemetryRecorder>();
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

    /// <summary>
    /// The conversation budget is chosen by <c>AppConfig.AI.Conversations.Provider</c> alongside the
    /// conversation store and the turn lease — all three have to agree on how far a conversation reaches
    /// — so Infrastructure.AI owns the registration. A default registered here as well would leave two
    /// registrations for one interface, with the winner decided by the order the composition root
    /// happens to add the layers, and a host would get a per-process ceiling from a durable
    /// configuration without anything failing.
    /// </summary>
    [Fact]
    public void AddApplicationAIDependencies_DoesNotRegisterConversationBudgetTracker()
    {
        var services = CreateServicesWithAIDependencies();

        services.Should().NotContain(d => d.ServiceType == typeof(IConversationBudgetTracker));
    }

    [Fact]
    public void AddApplicationAIDependencies_RegistersProgressEvaluator_AsScoped()
    {
        var services = CreateServicesWithAIDependencies();

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IProgressEvaluator));
        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Scoped);
        descriptor.ImplementationType.Should().Be(typeof(ProgressEvaluator));
    }

    [Fact]
    public void AddApplicationAIDependencies_RegistersGovernanceTraceRecorder_AsScoped()
    {
        // Scoped, and it matters which: the trail is per turn. A singleton would accumulate every
        // turn of every conversation in the process into one trace, and a transient would give each
        // stage of the same turn a private trail nobody else could read — the governor's decisions
        // and the loop guard's escalations would each land somewhere the turn handler never looks.
        var services = CreateServicesWithAIDependencies();

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IGovernanceTraceRecorder));
        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Scoped);
        descriptor.ImplementationType.Should().Be(typeof(GovernanceTraceRecorder));
    }

    [Fact]
    public void AddApplicationAIDependencies_RegistersToolCallAdmissionPipeline_AsScoped()
    {
        var services = CreateServicesWithAIDependencies();

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IToolCallAdmissionPipeline));
        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Scoped);
        descriptor.ImplementationType.Should().Be(typeof(ToolCallAdmissionPipeline));
    }

    /// <summary>
    /// The reason <see cref="DependencyInjection.AddToolCallAdmissionChain"/> exists: a fixture that
    /// wants to control one gate registers its own implementation, and the shared method's <c>TryAdd*</c>
    /// calls must never overwrite it with the production default. Without this, the whole premise of
    /// issue #349 (a fixture composes the same wiring the production root does, with its own mocks
    /// winning) does not hold. Both registration orders are pinned, and win for different reasons:
    /// registering first is the documented, load-bearing contract (<c>TryAdd*</c> sees the slot filled
    /// and skips it); registering after only wins because .NET DI resolves the LAST registration for a
    /// duplicated service type — a real behavior, but an incidental one the method's own doc tells
    /// callers not to rely on.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddToolCallAdmissionChain_AnOverrideIsRegistered_ItWinsRegardlessOfOrder(
        bool registerOverrideBeforeCallingTheChain)
    {
        var services = new ServiceCollection();
        var fixtureGovernor = Mock.Of<IToolInvocationGovernor>();

        if (registerOverrideBeforeCallingTheChain)
            services.AddSingleton(fixtureGovernor);

        services.AddToolCallAdmissionChain();

        if (!registerOverrideBeforeCallingTheChain)
            services.AddSingleton(fixtureGovernor);

        // Resolve, not just inspect the descriptor list: a duplicated registration still leaves the
        // fixture's descriptor findable by FirstOrDefault regardless of which one wins at resolution
        // time. GetRequiredService is what a real caller sees.
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IToolInvocationGovernor>().Should().BeSameAs(fixtureGovernor,
            "the fixture's own registration must survive, whether TryAdd skipped it or DI's " +
            "last-registration-wins resolution picked it — only the FIRST case is a guaranteed contract");
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
    public void AddApplicationAIDependencies_RegistersFifteenPipelineBehaviors()
    {
        var services = CreateServicesWithAIDependencies();

        var behaviors = services
            .Where(d => d.ServiceType.IsGenericType &&
                        d.ServiceType.GetGenericTypeDefinition() == typeof(MediatR.IPipelineBehavior<,>))
            .ToList();

        // 15 = the prior 16 minus ToolPermissionBehavior + GovernancePolicyBehavior (removed once
        // IToolInvocationGovernor took over tool authorization on the live tool path — nothing in
        // production implements IToolRequest, so those behaviors never fired), plus the post-turn
        // WorkEpisodeCaptureBehavior (self-improving work memory).
        behaviors.Should().HaveCount(15);
        behaviors.Should().OnlyContain(d => d.Lifetime == ServiceLifetime.Transient);
    }

    [Fact]
    public void AddApplicationAIDependencies_RegistersToolChainBuilder_AsSingleton()
    {
        var services = CreateServicesWithAIDependencies();

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IToolChainBuilder));
        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
        descriptor.ImplementationType.Should().Be(typeof(ToolChainBuilder));
    }

    [Fact]
    public void AddApplicationAIDependencies_RegistersSkillPrerequisiteResolver_AsSingleton()
    {
        var services = CreateServicesWithAIDependencies();

        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ISkillPrerequisiteResolver));
        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
        descriptor.ImplementationType.Should().Be(typeof(SkillPrerequisiteResolver));
    }

    [Fact]
    public void AddApplicationAIDependencies_RegistersAmbientRequestScope_AsSingleton()
    {
        var services = CreateServicesWithAIDependencies();

        var descriptor = services.FirstOrDefault(d =>
            d.ServiceType == typeof(Application.AI.Common.Interfaces.IAmbientRequestScope));
        descriptor.Should().NotBeNull();
        descriptor!.Lifetime.Should().Be(ServiceLifetime.Singleton);
        descriptor.ImplementationType.Should().Be(typeof(Application.AI.Common.Services.AmbientRequestScope));
    }

    [Fact]
    public void AddApplicationAIDependencies_RegistersGovernanceBehaviorMetric_InNonKeyedMetricSet()
    {
        // EvalRunner builds its metric map from IEnumerable<IEvalMetric>; keyed registrations are
        // invisible to IEnumerable<T>. A keyed-only registration would make the metric look present
        // but never run (EvalRunner silently skips unknown metric keys). Guard the non-keyed path.
        var provider = CreateServicesWithAIDependencies().BuildServiceProvider();

        var metrics = provider.GetServices<IEvalMetric>();

        metrics.Should().ContainSingle(m => m.Key == "governance.behavior");
    }

    [Fact]
    public void AddApplicationAIDependencies_RegistersOwaspMetrics_InNonKeyedMetricSet()
    {
        // The 10 OWASP metrics were registered keyed-only until #436, invisible to the same
        // IEnumerable<IEvalMetric> enumeration EvalRunner resolves from — every case in
        // eval-datasets/owasp-agentic-top-10.yaml silently scored 0.0/Warn instead of running.
        var provider = CreateServicesWithAIDependencies().BuildServiceProvider();

        var keys = provider.GetServices<IEvalMetric>().Select(m => m.Key);

        keys.Should().Contain([
            "owasp.asi01.goal_hijack", "owasp.asi02.tool_misuse", "owasp.asi03.privilege_abuse",
            "owasp.asi04.supply_chain", "owasp.asi05.code_exec", "owasp.asi06.memory_poison",
            "owasp.asi07.inter_agent", "owasp.asi08.cascading", "owasp.asi09.human_trust",
            "owasp.asi10.rogue_agent",
        ]);
    }

    [Fact]
    public void AddApplicationAIDependencies_ReturnsServiceCollection_ForChaining()
    {
        var services = new ServiceCollection();
        var returned = services.AddApplicationAIDependencies();

        returned.Should().BeSameAs(services);
    }
}
