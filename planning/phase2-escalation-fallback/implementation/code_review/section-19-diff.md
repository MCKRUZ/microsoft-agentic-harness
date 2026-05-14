diff --git a/src/Content/Application/Application.Core/DependencyInjection.cs b/src/Content/Application/Application.Core/DependencyInjection.cs
index e3c6566..a6037ee 100644
--- a/src/Content/Application/Application.Core/DependencyInjection.cs
+++ b/src/Content/Application/Application.Core/DependencyInjection.cs
@@ -1,9 +1,12 @@
+using Application.AI.Common.Interfaces.Escalation;
 using Application.AI.Common.Interfaces.Permissions;
+using Application.Core.Escalation.Strategies;
 using Application.Core.Permissions;
 using Application.Core.Workflows.Governance;
 using Application.Core.Workflows.KnowledgeGraph;
 using Application.Core.Workflows.MetaHarness;
 using Application.Core.Workflows.Rag;
+using Domain.AI.Escalation;
 using FluentValidation;
 using MediatR;
 using Microsoft.Agents.AI.Workflows;
@@ -42,6 +45,11 @@ public static class DependencyInjection
 		// Autonomy tier rule provider — generates baseline permission rules from agent tier
 		services.AddSingleton<IPermissionRuleProvider, AutonomyTierRuleProvider>();
 
+		// Approval strategies — keyed by ApprovalStrategyType for IEscalationService to resolve
+		services.AddKeyedSingleton<IApprovalStrategy>(ApprovalStrategyType.AnyOf, (_, _) => new AnyOfApprovalStrategy());
+		services.AddKeyedSingleton<IApprovalStrategy>(ApprovalStrategyType.AllOf, (_, _) => new AllOfApprovalStrategy());
+		services.AddKeyedSingleton<IApprovalStrategy>(ApprovalStrategyType.Quorum, (_, _) => new QuorumApprovalStrategy());
+
 		services.AddWorkflowDependencies();
 
 		return services;
diff --git a/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs b/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs
index 4721ce5..01021b4 100644
--- a/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs
+++ b/src/Content/Infrastructure/Infrastructure.AI/DependencyInjection.cs
@@ -4,7 +4,11 @@ using Application.AI.Common.Interfaces.MetaHarness;
 using Application.AI.Common.Interfaces.Skills;
 using Application.AI.Common.Interfaces.Memory;
 using Application.AI.Common.Interfaces.Traces;
+using Application.AI.Common.Interfaces.Escalation;
+using Application.AI.Common.Interfaces.Resilience;
+using Infrastructure.AI.Escalation;
 using Infrastructure.AI.Memory;
+using Infrastructure.AI.Resilience;
 using Infrastructure.AI.Security;
 using Infrastructure.AI.Traces;
 using Application.AI.Common.Interfaces.Agent;
@@ -243,9 +247,43 @@ public static class DependencyInjection
         // Regression suite service — scoped to match evaluation lifecycle
         services.AddScoped<IRegressionSuiteService, FileSystemRegressionSuiteService>();
 
+        RegisterEscalationServices(services);
+        RegisterResilienceServices(services, appConfig);
+
         return services;
     }
 
+    /// <summary>
+    /// Registers escalation pipeline services: service, audit store, composite notifier,
+    /// and no-op notification channel stubs.
+    /// </summary>
+    private static void RegisterEscalationServices(IServiceCollection services)
+    {
+        services.AddSingleton<IEscalationService, DefaultEscalationService>();
+        services.AddSingleton<IEscalationAuditStore, JsonlEscalationAuditStore>();
+        services.AddSingleton<IEscalationNotifier, CompositeEscalationNotifier>();
+        services.AddSingleton<IEscalationNotificationChannel, NoOpSlackNotifier>();
+        services.AddSingleton<IEscalationNotificationChannel, NoOpTeamsNotifier>();
+    }
+
+    /// <summary>
+    /// Registers resilience pipeline services: health monitor, capability registry,
+    /// resilient provider, and conditionally the retry queue hosted service.
+    /// </summary>
+    private static void RegisterResilienceServices(IServiceCollection services, AppConfig appConfig)
+    {
+        services.AddSingleton<PollyProviderHealthMonitor>();
+        services.AddSingleton<IProviderHealthMonitor>(sp => sp.GetRequiredService<PollyProviderHealthMonitor>());
+        services.AddSingleton<ProviderCapabilityRegistry>();
+        services.AddSingleton<IResilientChatClientProvider, ResilientChatClientProvider>();
+
+        if (appConfig.AI.Resilience.Enabled)
+        {
+            services.AddSingleton<LlmRetryQueue>();
+            services.AddHostedService(sp => sp.GetRequiredService<LlmRetryQueue>());
+        }
+    }
+
     private static void RegisterAIClients(IServiceCollection services, AppConfig appConfig)
     {
         var framework = appConfig.AI.AgentFramework;
diff --git a/src/Content/Presentation/Presentation.AgentHub/DependencyInjection.cs b/src/Content/Presentation/Presentation.AgentHub/DependencyInjection.cs
index d57dd5b..0c38047 100644
--- a/src/Content/Presentation/Presentation.AgentHub/DependencyInjection.cs
+++ b/src/Content/Presentation/Presentation.AgentHub/DependencyInjection.cs
@@ -1,6 +1,7 @@
 using System.Diagnostics;
 using System.Threading.RateLimiting;
 using Application.AI.Common.Interfaces;
+using Application.AI.Common.Interfaces.Escalation;
 using Microsoft.AspNetCore.Authentication;
 using Microsoft.AspNetCore.Authentication.JwtBearer;
 using Microsoft.AspNetCore.Http;
@@ -9,9 +10,11 @@ using Microsoft.Extensions.DependencyInjection.Extensions;
 using Microsoft.Identity.Web;
 using OpenTelemetry;
 using OpenTelemetry.Trace;
+using Presentation.AgentHub.AgUi;
 using Presentation.AgentHub.Auth;
 using Presentation.AgentHub.Hubs;
 using Presentation.AgentHub.Interfaces;
+using Presentation.AgentHub.Notifications;
 using Presentation.AgentHub.Config;
 using Presentation.AgentHub.Services;
 using Presentation.AgentHub.Telemetry;
@@ -179,6 +182,9 @@ public static class DependencyInjection
         // Singleton: ConversationLockRegistry must outlive hub instances (hubs are transient).
         services.AddSingleton<ConversationLockRegistry>();
 
+        services.AddSingleton<IAgUiEventWriterAccessor, AgUiEventWriterAccessor>();
+        services.AddSingleton<IEscalationNotificationChannel, AgUiEscalationNotifier>();
+
         // Scoped: AgUiRunHandler takes per-request dependencies (ClaimsPrincipal, CancellationToken).
         services.AddScoped<AgUi.AgUiRunHandler>();
 
diff --git a/src/Content/Presentation/Presentation.Common/Extensions/IServiceCollectionExtensions.cs b/src/Content/Presentation/Presentation.Common/Extensions/IServiceCollectionExtensions.cs
index 0300cfe..df1dedf 100644
--- a/src/Content/Presentation/Presentation.Common/Extensions/IServiceCollectionExtensions.cs
+++ b/src/Content/Presentation/Presentation.Common/Extensions/IServiceCollectionExtensions.cs
@@ -4,6 +4,8 @@ using Application.Common.Interfaces.Security;
 using Application.Core;
 using Domain.Common.Config;
 using Domain.Common.Config.AI;
+using Domain.Common.Config.AI.Governance;
+using Domain.Common.Config.AI.Resilience;
 using Domain.Common.Config.Azure;
 using Domain.Common.Config.Cache;
 using Domain.Common.Config.Connectors;
@@ -79,6 +81,8 @@ public static class IServiceCollectionExtensions
         services.Configure<AIConfig>(configuration.GetSection("AppConfig:AI"));
         services.Configure<AzureConfig>(configuration.GetSection("AppConfig:Azure"));
         services.Configure<CacheConfig>(configuration.GetSection("AppConfig:Cache"));
+        services.Configure<EscalationConfig>(configuration.GetSection("AppConfig:AI:Governance:Escalation"));
+        services.Configure<ResilienceConfig>(configuration.GetSection("AppConfig:AI:Resilience"));
 
         return services;
     }
diff --git a/src/Content/Tests/Application.Core.Tests/DependencyInjectionTests.cs b/src/Content/Tests/Application.Core.Tests/DependencyInjectionTests.cs
index cc46588..5adc7f9 100644
--- a/src/Content/Tests/Application.Core.Tests/DependencyInjectionTests.cs
+++ b/src/Content/Tests/Application.Core.Tests/DependencyInjectionTests.cs
@@ -1,7 +1,10 @@
+using Application.AI.Common.Interfaces.Escalation;
 using Application.Core.CQRS.Agents.ExecuteAgentTurn;
 using Application.Core.CQRS.Agents.RunConversation;
 using Application.Core.CQRS.Agents.RunOrchestratedTask;
 using Application.Core.CQRS.MetaHarness;
+using Application.Core.Escalation.Strategies;
+using Domain.AI.Escalation;
 using FluentAssertions;
 using FluentValidation;
 using Microsoft.Extensions.DependencyInjection;
@@ -77,4 +80,18 @@ public class DependencyInjectionTests
 
         result.Should().BeSameAs(services);
     }
+
+    [Theory]
+    [InlineData(ApprovalStrategyType.AnyOf, typeof(AnyOfApprovalStrategy))]
+    [InlineData(ApprovalStrategyType.AllOf, typeof(AllOfApprovalStrategy))]
+    [InlineData(ApprovalStrategyType.Quorum, typeof(QuorumApprovalStrategy))]
+    public void AddApplicationCoreDependencies_RegistersApprovalStrategies_KeyedByType(
+        ApprovalStrategyType key, Type expectedType)
+    {
+        using var provider = BuildProvider();
+
+        var strategy = provider.GetKeyedService<IApprovalStrategy>(key);
+
+        strategy.Should().NotBeNull().And.BeOfType(expectedType);
+    }
 }
diff --git a/src/Content/Tests/Infrastructure.AI.Tests/DependencyInjectionTests.cs b/src/Content/Tests/Infrastructure.AI.Tests/DependencyInjectionTests.cs
index 5c64819..a8a44d3 100644
--- a/src/Content/Tests/Infrastructure.AI.Tests/DependencyInjectionTests.cs
+++ b/src/Content/Tests/Infrastructure.AI.Tests/DependencyInjectionTests.cs
@@ -1,8 +1,14 @@
 using Application.AI.Common.Interfaces;
 using Application.AI.Common.Interfaces.Agent;
+using Application.AI.Common.Interfaces.Escalation;
+using Application.AI.Common.Interfaces.Resilience;
 using Domain.Common.Config;
+using Domain.Common.Config.AI.Resilience;
 using FluentAssertions;
+using Infrastructure.AI.Escalation;
+using Infrastructure.AI.Resilience;
 using Microsoft.Extensions.DependencyInjection;
+using Microsoft.Extensions.Hosting;
 using Microsoft.Extensions.Logging;
 using Microsoft.Extensions.Options;
 using Xunit;
@@ -74,6 +80,118 @@ public sealed class DependencyInjectionTests
         factory.IsAvailable(Domain.Common.Config.AI.AIAgentFrameworkClientType.OpenAI).Should().BeFalse();
     }
 
+    [Fact]
+    public void AddInfrastructureAIDependencies_RegistersIEscalationService()
+    {
+        var services = CreateBaseServices();
+        services.AddInfrastructureAIDependencies(new AppConfig());
+        using var provider = services.BuildServiceProvider();
+
+        var svc = provider.GetService<IEscalationService>();
+
+        svc.Should().NotBeNull().And.BeOfType<DefaultEscalationService>();
+    }
+
+    [Fact]
+    public void AddInfrastructureAIDependencies_RegistersIEscalationAuditStore()
+    {
+        var services = CreateBaseServices();
+        services.AddInfrastructureAIDependencies(new AppConfig());
+        using var provider = services.BuildServiceProvider();
+
+        var store = provider.GetService<IEscalationAuditStore>();
+
+        store.Should().NotBeNull().And.BeOfType<JsonlEscalationAuditStore>();
+    }
+
+    [Fact]
+    public void AddInfrastructureAIDependencies_RegistersIEscalationNotifier()
+    {
+        var services = CreateBaseServices();
+        services.AddInfrastructureAIDependencies(new AppConfig());
+        using var provider = services.BuildServiceProvider();
+
+        var notifier = provider.GetService<IEscalationNotifier>();
+
+        notifier.Should().NotBeNull().And.BeOfType<CompositeEscalationNotifier>();
+    }
+
+    [Fact]
+    public void AddInfrastructureAIDependencies_RegistersNotificationChannels()
+    {
+        var services = CreateBaseServices();
+        services.AddInfrastructureAIDependencies(new AppConfig());
+        using var provider = services.BuildServiceProvider();
+
+        var channels = provider.GetServices<IEscalationNotificationChannel>().ToList();
+
+        channels.Should().Contain(c => c is NoOpSlackNotifier);
+        channels.Should().Contain(c => c is NoOpTeamsNotifier);
+    }
+
+    [Fact]
+    public void AddInfrastructureAIDependencies_RegistersIProviderHealthMonitor()
+    {
+        var services = CreateBaseServices();
+        services.AddInfrastructureAIDependencies(new AppConfig());
+        using var provider = services.BuildServiceProvider();
+
+        var monitor = provider.GetService<IProviderHealthMonitor>();
+
+        monitor.Should().NotBeNull().And.BeOfType<PollyProviderHealthMonitor>();
+    }
+
+    [Fact]
+    public void AddInfrastructureAIDependencies_RegistersIResilientChatClientProvider()
+    {
+        var services = CreateBaseServices();
+        services.AddInfrastructureAIDependencies(new AppConfig());
+        using var provider = services.BuildServiceProvider();
+
+        var resilientProvider = provider.GetService<IResilientChatClientProvider>();
+
+        resilientProvider.Should().NotBeNull().And.BeOfType<ResilientChatClientProvider>();
+    }
+
+    [Fact]
+    public void AddInfrastructureAIDependencies_ResilienceEnabled_RegistersLlmRetryQueueHostedService()
+    {
+        var config = new AppConfig { AI = { Resilience = new ResilienceConfig { Enabled = true } } };
+        var services = CreateBaseServices(config);
+        services.AddSingleton(TimeProvider.System);
+        services.AddInfrastructureAIDependencies(config);
+        using var provider = services.BuildServiceProvider();
+
+        var hostedServices = provider.GetServices<IHostedService>().ToList();
+
+        hostedServices.Should().Contain(s => s is LlmRetryQueue);
+    }
+
+    [Fact]
+    public void AddInfrastructureAIDependencies_ResilienceDisabled_DoesNotRegisterLlmRetryQueueHostedService()
+    {
+        var config = new AppConfig { AI = { Resilience = new ResilienceConfig { Enabled = false } } };
+        var services = CreateBaseServices(config);
+        services.AddInfrastructureAIDependencies(config);
+        using var provider = services.BuildServiceProvider();
+
+        var hostedServices = provider.GetServices<IHostedService>().ToList();
+
+        hostedServices.Should().NotContain(s => s is LlmRetryQueue);
+    }
+
+    [Fact]
+    public void AddInfrastructureAIDependencies_CompositeNotifier_DoesNotContainItself()
+    {
+        var services = CreateBaseServices();
+        services.AddInfrastructureAIDependencies(new AppConfig());
+        using var provider = services.BuildServiceProvider();
+
+        var channels = provider.GetServices<IEscalationNotificationChannel>().ToList();
+
+        channels.Should().NotContain(c => c is CompositeEscalationNotifier);
+    }
+
     /// <summary>
     /// Minimal IOptionsMonitor stub that returns a fixed value.
     /// </summary>
diff --git a/src/Content/Tests/Presentation.Common.Tests/Extensions/IServiceCollectionExtensionsTests.cs b/src/Content/Tests/Presentation.Common.Tests/Extensions/IServiceCollectionExtensionsTests.cs
index bc79167..aacafa6 100644
--- a/src/Content/Tests/Presentation.Common.Tests/Extensions/IServiceCollectionExtensionsTests.cs
+++ b/src/Content/Tests/Presentation.Common.Tests/Extensions/IServiceCollectionExtensionsTests.cs
@@ -1,10 +1,13 @@
 using Domain.Common.Config;
+using Domain.Common.Config.AI.Governance;
+using Domain.Common.Config.AI.Resilience;
 using Domain.Common.Config.Cache;
 using FluentAssertions;
 using Microsoft.Extensions.Caching.Distributed;
 using Microsoft.Extensions.Caching.Memory;
 using Microsoft.Extensions.Configuration;
 using Microsoft.Extensions.DependencyInjection;
+using Microsoft.Extensions.Options;
 using Presentation.Common.Extensions;
 using Xunit;
 
@@ -147,4 +150,44 @@ public sealed class IServiceCollectionExtensionsTests
 
         result.Should().BeSameAs(services);
     }
+
+    // -- EscalationConfig and ResilienceConfig bindings --
+
+    [Fact]
+    public void RegisterConfigSections_BindsEscalationConfig()
+    {
+        var services = new ServiceCollection();
+        var config = new ConfigurationBuilder()
+            .AddInMemoryCollection(new Dictionary<string, string?>
+            {
+                ["AppConfig:AI:Governance:Escalation:Enabled"] = "true",
+                ["AppConfig:AI:Governance:Escalation:DefaultTimeoutSeconds"] = "600",
+            })
+            .Build();
+
+        services.RegisterConfigSections(config);
+        var provider = services.BuildServiceProvider();
+
+        var escConfig = provider.GetRequiredService<IOptionsMonitor<EscalationConfig>>().CurrentValue;
+        escConfig.Enabled.Should().BeTrue();
+        escConfig.DefaultTimeoutSeconds.Should().Be(600);
+    }
+
+    [Fact]
+    public void RegisterConfigSections_BindsResilienceConfig()
+    {
+        var services = new ServiceCollection();
+        var config = new ConfigurationBuilder()
+            .AddInMemoryCollection(new Dictionary<string, string?>
+            {
+                ["AppConfig:AI:Resilience:Enabled"] = "true",
+            })
+            .Build();
+
+        services.RegisterConfigSections(config);
+        var provider = services.BuildServiceProvider();
+
+        var resConfig = provider.GetRequiredService<IOptionsMonitor<ResilienceConfig>>().CurrentValue;
+        resConfig.Enabled.Should().BeTrue();
+    }
 }
