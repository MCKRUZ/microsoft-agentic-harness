using Application.AI.Common.Interfaces.DriftDetection;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Learnings;
using MediatR;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.DriftDetection;
using Domain.Common.Config.AI.Learnings;
using FluentAssertions;
using Infrastructure.AI.DriftDetection;
using Infrastructure.AI.KnowledgeGraph;
using Infrastructure.AI.Learnings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests;

/// <summary>
/// DI registration tests for Phase 3 drift detection and learnings services.
/// Verifies that all services resolve correctly from the container.
/// </summary>
public sealed class DriftLearningsDiTests
{
    [Fact]
    public void DriftDetection_Service_Resolves()
    {
        var sp = CreateServiceProvider();

        var service = sp.GetService<IDriftDetectionService>();

        service.Should().NotBeNull();
        service.Should().BeOfType<DefaultDriftDetectionService>();
    }

    [Fact]
    public void DriftDetection_KeyedScorer_Resolves_Ewma()
    {
        var sp = CreateServiceProvider();

        var scorer = sp.GetRequiredKeyedService<IDriftScorer>("ewma");

        scorer.Should().NotBeNull();
        scorer.Should().BeOfType<EwmaDriftScorer>();
    }

    [Fact]
    public void DriftDetection_BaselineStore_Graph_Resolves()
    {
        var sp = CreateServiceProvider();

        var store = sp.GetRequiredKeyedService<IDriftBaselineStore>("graph");

        store.Should().NotBeNull();
        store.Should().BeOfType<GraphDriftBaselineStore>();
    }

    [Fact]
    public void DriftDetection_BaselineStore_InMemory_Resolves()
    {
        var sp = CreateServiceProvider();

        var store = sp.GetRequiredKeyedService<IDriftBaselineStore>("in_memory");

        store.Should().NotBeNull();
        store.Should().BeOfType<InMemoryDriftBaselineStore>();
    }

    [Fact]
    public void DriftDetection_BaselineStore_Default_ResolvesGraph()
    {
        var sp = CreateServiceProvider();

        var store = sp.GetService<IDriftBaselineStore>();

        store.Should().NotBeNull();
        store.Should().BeOfType<GraphDriftBaselineStore>();
    }

    [Fact]
    public void DriftDetection_AuditStore_Resolves()
    {
        var sp = CreateServiceProvider();

        var store = sp.GetService<IDriftAuditStore>();

        store.Should().NotBeNull();
        store.Should().BeOfType<JsonlDriftAuditStore>();
    }

    [Fact]
    public void DriftDetection_Notifier_Resolves()
    {
        var sp = CreateServiceProvider();

        var notifier = sp.GetService<IDriftNotifier>();

        notifier.Should().NotBeNull();
        notifier.Should().BeOfType<CompositeDriftNotifier>();
    }

    [Fact]
    public void DriftDetection_EwmaStateStore_Resolves()
    {
        var sp = CreateServiceProvider();

        var store = sp.GetService<IEwmaStateStore>();

        store.Should().NotBeNull();
        store.Should().BeOfType<GraphEwmaStateStore>();
    }

    [Fact]
    public void DriftEscalationBridge_RegisteredAsNotificationChannel()
    {
        var sp = CreateServiceProvider();

        var channels = sp.GetServices<IEscalationNotificationChannel>().ToList();

        channels.Should().Contain(c => c is DriftEscalationBridge);
    }

    [Fact]
    public void Learnings_DecayService_Resolves()
    {
        var sp = CreateServiceProvider();

        var service = sp.GetService<ILearningDecayService>();

        service.Should().NotBeNull();
        service.Should().BeOfType<DefaultLearningDecayService>();
    }

    [Fact]
    public void Learnings_DriftBridge_Resolves()
    {
        var sp = CreateServiceProvider();

        var bridge = sp.GetService<ILearningsDriftBridge>();

        bridge.Should().NotBeNull();
        bridge.Should().BeOfType<LearningsDriftBridge>();
    }

    [Fact]
    public void LearningsPruningService_RegisteredWhenEnabled()
    {
        var sp = CreateServiceProvider(learningsEnabled: true);

        var services = sp.GetServices<IHostedService>().ToList();

        services.Should().Contain(s => s is LearningsPruningBackgroundService);
    }

    [Fact]
    public void LearningsPruningService_NotRegisteredWhenDisabled()
    {
        var sp = CreateServiceProvider(learningsEnabled: false);

        var services = sp.GetServices<IHostedService>().ToList();

        services.Should().NotContain(s => s is LearningsPruningBackgroundService);
    }

    [Fact]
    public void AIConfig_BindsDriftDetectionConfig_Defaults()
    {
        var config = new AppConfig();

        config.AI.DriftDetection.Should().NotBeNull();
        config.AI.DriftDetection.Enabled.Should().BeTrue();
        config.AI.DriftDetection.EwmaLambda.Should().BeApproximately(0.2, 0.001);
    }

    [Fact]
    public void AIConfig_BindsLearningsConfig_Defaults()
    {
        var config = new AppConfig();

        config.AI.Learnings.Should().NotBeNull();
        config.AI.Learnings.Enabled.Should().BeTrue();
        config.AI.Learnings.StoreProvider.Should().Be("graph");
        config.AI.Learnings.BaselineAdjustmentThreshold.Should().BeApproximately(0.8, 0.001);
    }

    private static ServiceProvider CreateServiceProvider(bool learningsEnabled = true)
    {
        var appConfig = new AppConfig
        {
            AI = new AIConfig
            {
                DriftDetection = new DriftDetectionConfig(),
                Learnings = new LearningsConfig { Enabled = learningsEnabled }
            }
        };

        var services = new ServiceCollection();
        services.AddOptions();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IOptionsMonitor<AppConfig>>(new OptionsMonitorStub(appConfig));
        // Plugin loader (registered by AddInfrastructureAIDependencies) reads the AppConfig:AI
        // section as IOptionsMonitor<AIConfig>; mirror the real composition root so enumerating
        // IHostedService can construct PluginStartupLoader.
        services.AddSingleton<IOptionsMonitor<AIConfig>>(new AIConfigMonitorStub(appConfig.AI));
        services.AddSingleton<IOptionsMonitor<LearningsConfig>>(
            new LearningsConfigMonitorStub(appConfig.AI.Learnings));
        services.AddSingleton<ISender>(new Mock<ISender>().Object);

        // Register knowledge graph (provides IKnowledgeGraphStore for graph-backed stores)
        services.AddKnowledgeGraphDependencies(appConfig);

        // Register Infrastructure.AI services (drift, learnings, escalation, etc.)
        services.AddInfrastructureAIDependencies(appConfig);

        return services.BuildServiceProvider();
    }

    private sealed class OptionsMonitorStub(AppConfig config) : IOptionsMonitor<AppConfig>
    {
        public AppConfig CurrentValue => config;
        public AppConfig Get(string? name) => config;
        public IDisposable? OnChange(Action<AppConfig, string?> listener) => null;
    }

    private sealed class LearningsConfigMonitorStub(LearningsConfig config) : IOptionsMonitor<LearningsConfig>
    {
        public LearningsConfig CurrentValue => config;
        public LearningsConfig Get(string? name) => config;
        public IDisposable? OnChange(Action<LearningsConfig, string?> listener) => null;
    }

    private sealed class AIConfigMonitorStub(AIConfig config) : IOptionsMonitor<AIConfig>
    {
        public AIConfig CurrentValue => config;
        public AIConfig Get(string? name) => config;
        public IDisposable? OnChange(Action<AIConfig, string?> listener) => null;
    }
}
