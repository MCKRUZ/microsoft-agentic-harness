diff --git a/planning/phase3-quality-loop/implementation/deep_implement_config.json b/planning/phase3-quality-loop/implementation/deep_implement_config.json
index 8045c0e..a9874af 100644
--- a/planning/phase3-quality-loop/implementation/deep_implement_config.json
+++ b/planning/phase3-quality-loop/implementation/deep_implement_config.json
@@ -88,6 +88,14 @@
     "section-15-learnings-sse": {
       "status": "complete",
       "commit_hash": "204ead3"
+    },
+    "section-16-escalation-bridge": {
+      "status": "complete",
+      "commit_hash": "474400c"
+    },
+    "section-17-learnings-bridge": {
+      "status": "complete",
+      "commit_hash": "4b8197a"
     }
   },
   "pre_commit": {
diff --git a/src/Content/Infrastructure/Infrastructure.AI.KnowledgeGraph/DependencyInjection.cs b/src/Content/Infrastructure/Infrastructure.AI.KnowledgeGraph/DependencyInjection.cs
index 970a77f..015b387 100644
--- a/src/Content/Infrastructure/Infrastructure.AI.KnowledgeGraph/DependencyInjection.cs
+++ b/src/Content/Infrastructure/Infrastructure.AI.KnowledgeGraph/DependencyInjection.cs
@@ -1,4 +1,5 @@
 using Application.AI.Common.Interfaces.KnowledgeGraph;
+using Application.AI.Common.Interfaces.Learnings;
 using Application.AI.Common.Interfaces.RAG;
 using Application.AI.Common.Interfaces.Skills;
 using Domain.Common.Config;
@@ -156,6 +157,20 @@ public static class DependencyInjection
                     sp.GetRequiredService<ILogger<GraphSkillAmendmentProvider>>()));
         }
 
+        // --- Learnings Store (keyed DI) ---
+
+        services.AddKeyedSingleton<ILearningsStore>("graph", (sp, _) =>
+            new Learnings.GraphLearningsStore(
+                sp.GetRequiredService<IKnowledgeGraphStore>(),
+                sp.GetRequiredService<ILogger<Learnings.GraphLearningsStore>>()));
+
+        services.AddKeyedSingleton<ILearningsStore>("in_memory", (_, _) =>
+            new Learnings.InMemoryLearningsStore());
+
+        var learningsProvider = appConfig.AI.Learnings.StoreProvider;
+        services.AddSingleton<ILearningsStore>(sp =>
+            sp.GetRequiredKeyedService<ILearningsStore>(learningsProvider));
+
         return services;
     }
 }
diff --git a/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs b/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs
index 619c10e..c68269f 100644
--- a/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs
+++ b/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs
@@ -1,12 +1,16 @@
 using Application.AI.Common.Interfaces;
 using Application.AI.Common.Interfaces.A2A;
+using Application.AI.Common.Interfaces.DriftDetection;
+using Application.AI.Common.Interfaces.Learnings;
 using Application.AI.Common.Interfaces.MetaHarness;
 using Application.AI.Common.Interfaces.Skills;
 using Application.AI.Common.Interfaces.Memory;
 using Application.AI.Common.Interfaces.Traces;
 using Application.AI.Common.Interfaces.Escalation;
 using Application.AI.Common.Interfaces.Resilience;
+using Infrastructure.AI.DriftDetection;
 using Infrastructure.AI.Escalation;
+using Infrastructure.AI.Learnings;
 using Infrastructure.AI.Memory;
 using Infrastructure.AI.Resilience;
 using Infrastructure.AI.Security;
@@ -249,6 +253,8 @@ public static class DependencyInjection
 
         RegisterEscalationServices(services);
         RegisterResilienceServices(services, appConfig);
+        RegisterDriftDetectionServices(services);
+        RegisterLearningsServices(services, appConfig);
 
         return services;
     }
@@ -264,6 +270,7 @@ public static class DependencyInjection
         services.AddSingleton<IEscalationNotifier, CompositeEscalationNotifier>();
         services.AddSingleton<IEscalationNotificationChannel, NoOpSlackNotifier>();
         services.AddSingleton<IEscalationNotificationChannel, NoOpTeamsNotifier>();
+        services.AddSingleton<IEscalationNotificationChannel, DriftEscalationBridge>();
     }
 
     /// <summary>
@@ -285,6 +292,78 @@ public static class DependencyInjection
         }
     }
 
+    /// <summary>
+    /// Registers drift detection pipeline: scorer, baseline store, audit, notifier, EWMA state,
+    /// and the main detection service.
+    /// </summary>
+    private static void RegisterDriftDetectionServices(IServiceCollection services)
+    {
+        services.AddKeyedSingleton<IDriftScorer>("ewma", (sp, _) =>
+            new EwmaDriftScorer(
+                sp.GetRequiredService<IEwmaStateStore>(),
+                sp.GetRequiredService<IOptionsMonitor<AppConfig>>(),
+                sp.GetService<TimeProvider>() ?? TimeProvider.System,
+                sp.GetRequiredService<ILogger<EwmaDriftScorer>>()));
+
+        services.AddKeyedSingleton<IDriftBaselineStore>("graph", (sp, _) =>
+            new GraphDriftBaselineStore(
+                sp.GetRequiredService<Application.AI.Common.Interfaces.KnowledgeGraph.IKnowledgeGraphStore>(),
+                sp.GetRequiredService<ILogger<GraphDriftBaselineStore>>()));
+
+        services.AddKeyedSingleton<IDriftBaselineStore>("in_memory", (_, _) =>
+            new InMemoryDriftBaselineStore());
+
+        services.AddSingleton<IDriftBaselineStore>(sp =>
+            sp.GetRequiredKeyedService<IDriftBaselineStore>("graph"));
+
+        services.AddSingleton<IDriftAuditStore>(sp =>
+            new JsonlDriftAuditStore(
+                sp.GetRequiredService<IOptionsMonitor<AppConfig>>(),
+                sp.GetService<TimeProvider>() ?? TimeProvider.System,
+                sp.GetRequiredService<ILogger<JsonlDriftAuditStore>>()));
+
+        services.AddSingleton<IDriftNotifier, CompositeDriftNotifier>();
+
+        services.AddSingleton<IEwmaStateStore>(sp =>
+            new GraphEwmaStateStore(
+                sp.GetRequiredService<Application.AI.Common.Interfaces.KnowledgeGraph.IKnowledgeGraphStore>(),
+                sp.GetRequiredService<ILogger<GraphEwmaStateStore>>()));
+
+        services.AddSingleton<IDriftDetectionService>(sp =>
+            new DefaultDriftDetectionService(
+                sp.GetRequiredKeyedService<IDriftScorer>("ewma"),
+                sp.GetRequiredService<IDriftBaselineStore>(),
+                sp.GetRequiredService<IDriftAuditStore>(),
+                sp.GetRequiredService<IDriftNotifier>(),
+                sp.GetRequiredService<IEscalationService>(),
+                sp.GetRequiredService<Application.AI.Common.Interfaces.KnowledgeGraph.IKnowledgeGraphStore>(),
+                sp.GetRequiredService<IOptionsMonitor<AppConfig>>(),
+                sp.GetService<TimeProvider>() ?? TimeProvider.System,
+                sp.GetRequiredService<ILogger<DefaultDriftDetectionService>>()));
+    }
+
+    /// <summary>
+    /// Registers learnings subsystem: decay service, drift bridge, and conditional
+    /// pruning background service.
+    /// </summary>
+    private static void RegisterLearningsServices(IServiceCollection services, AppConfig appConfig)
+    {
+        services.AddSingleton<ILearningDecayService>(sp =>
+            new DefaultLearningDecayService(
+                sp.GetRequiredService<ILearningsStore>(),
+                sp.GetRequiredService<IOptionsMonitor<Domain.Common.Config.AI.Learnings.LearningsConfig>>(),
+                sp.GetService<TimeProvider>() ?? TimeProvider.System,
+                sp.GetRequiredService<ILogger<DefaultLearningDecayService>>()));
+
+        services.AddSingleton<ILearningsDriftBridge, LearningsDriftBridge>();
+
+        if (appConfig.AI.Learnings.Enabled)
+        {
+            services.AddSingleton<LearningsPruningBackgroundService>();
+            services.AddHostedService(sp => sp.GetRequiredService<LearningsPruningBackgroundService>());
+        }
+    }
+
     private static void RegisterAIClients(IServiceCollection services, AppConfig appConfig)
     {
         var framework = appConfig.AI.AgentFramework;
diff --git a/src/Content/Presentation/Presentation.AgentHub/DependencyInjection.cs b/src/Content/Presentation/Presentation.AgentHub/DependencyInjection.cs
index 0c38047..daf6566 100644
--- a/src/Content/Presentation/Presentation.AgentHub/DependencyInjection.cs
+++ b/src/Content/Presentation/Presentation.AgentHub/DependencyInjection.cs
@@ -1,7 +1,9 @@
 using System.Diagnostics;
 using System.Threading.RateLimiting;
 using Application.AI.Common.Interfaces;
+using Application.AI.Common.Interfaces.DriftDetection;
 using Application.AI.Common.Interfaces.Escalation;
+using Application.AI.Common.Interfaces.Learnings;
 using Microsoft.AspNetCore.Authentication;
 using Microsoft.AspNetCore.Authentication.JwtBearer;
 using Microsoft.AspNetCore.Http;
@@ -184,6 +186,8 @@ public static class DependencyInjection
 
         services.AddSingleton<IAgUiEventWriterAccessor, AgUiEventWriterAccessor>();
         services.AddSingleton<IEscalationNotificationChannel, AgUiEscalationNotifier>();
+        services.AddSingleton<IDriftNotificationChannel, AgUiDriftNotifier>();
+        services.AddSingleton<ILearningNotificationChannel, AgUiLearningNotifier>();
 
         // Scoped: AgUiRunHandler takes per-request dependencies (ClaimsPrincipal, CancellationToken).
         services.AddScoped<AgUi.AgUiRunHandler>();
diff --git a/src/Content/Presentation/Presentation.Common/Extensions/IServiceCollectionExtensions.cs b/src/Content/Presentation/Presentation.Common/Extensions/IServiceCollectionExtensions.cs
index df1dedf..5c7e70f 100644
--- a/src/Content/Presentation/Presentation.Common/Extensions/IServiceCollectionExtensions.cs
+++ b/src/Content/Presentation/Presentation.Common/Extensions/IServiceCollectionExtensions.cs
@@ -15,6 +15,7 @@ using Domain.Common.Config.Observability;
 using Infrastructure.AI;
 using Infrastructure.AI.Connectors;
 using Infrastructure.AI.Governance;
+using Infrastructure.AI.KnowledgeGraph;
 using Infrastructure.AI.RAG;
 using Infrastructure.AI.MCP;
 using Infrastructure.APIAccess;
@@ -83,6 +84,10 @@ public static class IServiceCollectionExtensions
         services.Configure<CacheConfig>(configuration.GetSection("AppConfig:Cache"));
         services.Configure<EscalationConfig>(configuration.GetSection("AppConfig:AI:Governance:Escalation"));
         services.Configure<ResilienceConfig>(configuration.GetSection("AppConfig:AI:Resilience"));
+        services.Configure<Domain.Common.Config.AI.DriftDetection.DriftDetectionConfig>(
+            configuration.GetSection("AppConfig:AI:DriftDetection"));
+        services.Configure<Domain.Common.Config.AI.Learnings.LearningsConfig>(
+            configuration.GetSection("AppConfig:AI:Learnings"));
 
         return services;
     }
@@ -244,6 +249,7 @@ public static class IServiceCollectionExtensions
 
         // Infrastructure layer
         services.AddInfrastructureCommonDependencies();
+        services.AddKnowledgeGraphDependencies(appConfig);
         // RAG must register before Infrastructure.AI — tool registrations depend on IRagOrchestrator
         services.AddRagDependencies(appConfig);
         services.AddInfrastructureAIDependencies(appConfig);
diff --git a/src/Content/Presentation/Presentation.Common/Presentation.Common.csproj b/src/Content/Presentation/Presentation.Common/Presentation.Common.csproj
index 9ce30e0..7e87909 100644
--- a/src/Content/Presentation/Presentation.Common/Presentation.Common.csproj
+++ b/src/Content/Presentation/Presentation.Common/Presentation.Common.csproj
@@ -15,6 +15,7 @@
     <!-- Infrastructure layer -->
     <ProjectReference Include="..\..\Infrastructure\Infrastructure.Common\Infrastructure.Common.csproj" />
     <ProjectReference Include="..\..\Infrastructure\Infrastructure.AI\Infrastructure.AI.csproj" />
+    <ProjectReference Include="..\..\Infrastructure\Infrastructure.AI.KnowledgeGraph\Infrastructure.AI.KnowledgeGraph.csproj" />
     <ProjectReference Include="..\..\Infrastructure\Infrastructure.AI.Governance\Infrastructure.AI.Governance.csproj" />
     <ProjectReference Include="..\..\Infrastructure\Infrastructure.AI.RAG\Infrastructure.AI.RAG.csproj" />
     <ProjectReference Include="..\..\Infrastructure\Infrastructure.AI.Connectors\Infrastructure.AI.Connectors.csproj" />
diff --git a/src/Content/Tests/Infrastructure.AI.Tests/DependencyInjectionTests.cs b/src/Content/Tests/Infrastructure.AI.Tests/DependencyInjectionTests.cs
index a8a44d3..ce2a35f 100644
--- a/src/Content/Tests/Infrastructure.AI.Tests/DependencyInjectionTests.cs
+++ b/src/Content/Tests/Infrastructure.AI.Tests/DependencyInjectionTests.cs
@@ -6,11 +6,14 @@ using Domain.Common.Config;
 using Domain.Common.Config.AI.Resilience;
 using FluentAssertions;
 using Infrastructure.AI.Escalation;
+using Infrastructure.AI.KnowledgeGraph;
 using Infrastructure.AI.Resilience;
+using MediatR;
 using Microsoft.Extensions.DependencyInjection;
 using Microsoft.Extensions.Hosting;
 using Microsoft.Extensions.Logging;
 using Microsoft.Extensions.Options;
+using Moq;
 using Xunit;
 
 namespace Infrastructure.AI.Tests;
@@ -24,8 +27,11 @@ public sealed class DependencyInjectionTests
 
         // Register dependencies that Infrastructure.AI expects
         services.AddLogging(b => b.AddConsole());
+        services.AddSingleton(TimeProvider.System);
         services.AddSingleton<IOptionsMonitor<AppConfig>>(
             new OptionsMonitorStub(config));
+        services.AddSingleton<ISender>(new Mock<ISender>().Object);
+        services.AddKnowledgeGraphDependencies(config);
 
         return services;
     }
diff --git a/src/Content/Tests/Infrastructure.AI.Tests/DriftLearningsDiTests.cs b/src/Content/Tests/Infrastructure.AI.Tests/DriftLearningsDiTests.cs
new file mode 100644
index 0000000..e1cbf03
--- /dev/null
+++ b/src/Content/Tests/Infrastructure.AI.Tests/DriftLearningsDiTests.cs
@@ -0,0 +1,233 @@
+using Application.AI.Common.Interfaces.DriftDetection;
+using Application.AI.Common.Interfaces.Escalation;
+using Application.AI.Common.Interfaces.Learnings;
+using MediatR;
+using Domain.Common.Config;
+using Domain.Common.Config.AI;
+using Domain.Common.Config.AI.DriftDetection;
+using Domain.Common.Config.AI.Learnings;
+using FluentAssertions;
+using Infrastructure.AI.DriftDetection;
+using Infrastructure.AI.KnowledgeGraph;
+using Infrastructure.AI.Learnings;
+using Microsoft.Extensions.DependencyInjection;
+using Microsoft.Extensions.Hosting;
+using Microsoft.Extensions.Logging;
+using Microsoft.Extensions.Logging.Abstractions;
+using Microsoft.Extensions.Options;
+using Moq;
+using Xunit;
+
+namespace Infrastructure.AI.Tests;
+
+/// <summary>
+/// DI registration tests for Phase 3 drift detection and learnings services.
+/// Verifies that all services resolve correctly from the container.
+/// </summary>
+public sealed class DriftLearningsDiTests
+{
+    [Fact]
+    public void DriftDetection_Service_Resolves()
+    {
+        var sp = CreateServiceProvider();
+
+        var service = sp.GetService<IDriftDetectionService>();
+
+        service.Should().NotBeNull();
+        service.Should().BeOfType<DefaultDriftDetectionService>();
+    }
+
+    [Fact]
+    public void DriftDetection_KeyedScorer_Resolves_Ewma()
+    {
+        var sp = CreateServiceProvider();
+
+        var scorer = sp.GetRequiredKeyedService<IDriftScorer>("ewma");
+
+        scorer.Should().NotBeNull();
+        scorer.Should().BeOfType<EwmaDriftScorer>();
+    }
+
+    [Fact]
+    public void DriftDetection_BaselineStore_Graph_Resolves()
+    {
+        var sp = CreateServiceProvider();
+
+        var store = sp.GetRequiredKeyedService<IDriftBaselineStore>("graph");
+
+        store.Should().NotBeNull();
+        store.Should().BeOfType<GraphDriftBaselineStore>();
+    }
+
+    [Fact]
+    public void DriftDetection_BaselineStore_InMemory_Resolves()
+    {
+        var sp = CreateServiceProvider();
+
+        var store = sp.GetRequiredKeyedService<IDriftBaselineStore>("in_memory");
+
+        store.Should().NotBeNull();
+        store.Should().BeOfType<InMemoryDriftBaselineStore>();
+    }
+
+    [Fact]
+    public void DriftDetection_BaselineStore_Default_ResolvesGraph()
+    {
+        var sp = CreateServiceProvider();
+
+        var store = sp.GetService<IDriftBaselineStore>();
+
+        store.Should().NotBeNull();
+        store.Should().BeOfType<GraphDriftBaselineStore>();
+    }
+
+    [Fact]
+    public void DriftDetection_AuditStore_Resolves()
+    {
+        var sp = CreateServiceProvider();
+
+        var store = sp.GetService<IDriftAuditStore>();
+
+        store.Should().NotBeNull();
+        store.Should().BeOfType<JsonlDriftAuditStore>();
+    }
+
+    [Fact]
+    public void DriftDetection_Notifier_Resolves()
+    {
+        var sp = CreateServiceProvider();
+
+        var notifier = sp.GetService<IDriftNotifier>();
+
+        notifier.Should().NotBeNull();
+        notifier.Should().BeOfType<CompositeDriftNotifier>();
+    }
+
+    [Fact]
+    public void DriftDetection_EwmaStateStore_Resolves()
+    {
+        var sp = CreateServiceProvider();
+
+        var store = sp.GetService<IEwmaStateStore>();
+
+        store.Should().NotBeNull();
+        store.Should().BeOfType<GraphEwmaStateStore>();
+    }
+
+    [Fact]
+    public void DriftEscalationBridge_RegisteredAsNotificationChannel()
+    {
+        var sp = CreateServiceProvider();
+
+        var channels = sp.GetServices<IEscalationNotificationChannel>().ToList();
+
+        channels.Should().Contain(c => c is DriftEscalationBridge);
+    }
+
+    [Fact]
+    public void Learnings_DecayService_Resolves()
+    {
+        var sp = CreateServiceProvider();
+
+        var service = sp.GetService<ILearningDecayService>();
+
+        service.Should().NotBeNull();
+        service.Should().BeOfType<DefaultLearningDecayService>();
+    }
+
+    [Fact]
+    public void Learnings_DriftBridge_Resolves()
+    {
+        var sp = CreateServiceProvider();
+
+        var bridge = sp.GetService<ILearningsDriftBridge>();
+
+        bridge.Should().NotBeNull();
+        bridge.Should().BeOfType<LearningsDriftBridge>();
+    }
+
+    [Fact]
+    public void LearningsPruningService_RegisteredWhenEnabled()
+    {
+        var sp = CreateServiceProvider(learningsEnabled: true);
+
+        var services = sp.GetServices<IHostedService>().ToList();
+
+        services.Should().Contain(s => s is LearningsPruningBackgroundService);
+    }
+
+    [Fact]
+    public void LearningsPruningService_NotRegisteredWhenDisabled()
+    {
+        var sp = CreateServiceProvider(learningsEnabled: false);
+
+        var services = sp.GetServices<IHostedService>().ToList();
+
+        services.Should().NotContain(s => s is LearningsPruningBackgroundService);
+    }
+
+    [Fact]
+    public void AIConfig_BindsDriftDetectionConfig_Defaults()
+    {
+        var config = new AppConfig();
+
+        config.AI.DriftDetection.Should().NotBeNull();
+        config.AI.DriftDetection.Enabled.Should().BeTrue();
+        config.AI.DriftDetection.EwmaLambda.Should().BeApproximately(0.2, 0.001);
+    }
+
+    [Fact]
+    public void AIConfig_BindsLearningsConfig_Defaults()
+    {
+        var config = new AppConfig();
+
+        config.AI.Learnings.Should().NotBeNull();
+        config.AI.Learnings.Enabled.Should().BeTrue();
+        config.AI.Learnings.StoreProvider.Should().Be("graph");
+        config.AI.Learnings.BaselineAdjustmentThreshold.Should().BeApproximately(0.8, 0.001);
+    }
+
+    private static ServiceProvider CreateServiceProvider(bool learningsEnabled = true)
+    {
+        var appConfig = new AppConfig
+        {
+            AI = new AIConfig
+            {
+                DriftDetection = new DriftDetectionConfig(),
+                Learnings = new LearningsConfig { Enabled = learningsEnabled }
+            }
+        };
+
+        var services = new ServiceCollection();
+        services.AddOptions();
+        services.AddSingleton(TimeProvider.System);
+        services.AddSingleton(NullLoggerFactory.Instance);
+        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
+        services.AddSingleton<IOptionsMonitor<AppConfig>>(new OptionsMonitorStub(appConfig));
+        services.AddSingleton<IOptionsMonitor<LearningsConfig>>(
+            new LearningsConfigMonitorStub(appConfig.AI.Learnings));
+        services.AddSingleton<ISender>(new Mock<ISender>().Object);
+
+        // Register knowledge graph (provides IKnowledgeGraphStore for graph-backed stores)
+        services.AddKnowledgeGraphDependencies(appConfig);
+
+        // Register Infrastructure.AI services (drift, learnings, escalation, etc.)
+        services.AddInfrastructureAIDependencies(appConfig);
+
+        return services.BuildServiceProvider();
+    }
+
+    private sealed class OptionsMonitorStub(AppConfig config) : IOptionsMonitor<AppConfig>
+    {
+        public AppConfig CurrentValue => config;
+        public AppConfig Get(string? name) => config;
+        public IDisposable? OnChange(Action<AppConfig, string?> listener) => null;
+    }
+
+    private sealed class LearningsConfigMonitorStub(LearningsConfig config) : IOptionsMonitor<LearningsConfig>
+    {
+        public LearningsConfig CurrentValue => config;
+        public LearningsConfig Get(string? name) => config;
+        public IDisposable? OnChange(Action<LearningsConfig, string?> listener) => null;
+    }
+}
diff --git a/src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj b/src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj
index 52b5326..9f9e258 100644
--- a/src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj
+++ b/src/Content/Tests/Infrastructure.AI.Tests/Infrastructure.AI.Tests.csproj
@@ -25,6 +25,7 @@
 
   <ItemGroup>
     <ProjectReference Include="../../Infrastructure/Infrastructure.AI/Infrastructure.AI.csproj" />
+    <ProjectReference Include="../../Infrastructure/Infrastructure.AI.KnowledgeGraph/Infrastructure.AI.KnowledgeGraph.csproj" />
     <ProjectReference Include="../../Infrastructure/Infrastructure.AI.MCP/Infrastructure.AI.MCP.csproj" />
     <ProjectReference Include="../../Application/Application.AI.Common/Application.AI.Common.csproj" />
     <ProjectReference Include="../../Application/Application.Core/Application.Core.csproj" />
