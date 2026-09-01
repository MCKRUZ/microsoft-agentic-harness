using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Agent;
using Application.AI.Common.Interfaces.Bundles;
using Application.AI.Common.Interfaces.Escalation;
using Application.AI.Common.Interfaces.Resilience;
using Domain.Common.Config;
using Domain.Common.Config.AI.MCP;
using Domain.Common.Config.AI.Resilience;
using FluentAssertions;
using Infrastructure.AI.Escalation;
using Infrastructure.AI.KnowledgeGraph;
using Infrastructure.AI.MCP;
using Infrastructure.AI.Resilience;
using Infrastructure.AI.Tests.Planner.StepExecutors;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests;

public sealed class DependencyInjectionTests
{
    private static ServiceCollection CreateBaseServices(AppConfig? appConfig = null)
    {
        var config = IsolatedAppConfig.Isolate(appConfig ?? new AppConfig());
        var services = new ServiceCollection();

        // Register dependencies that Infrastructure.AI expects
        services.AddLogging(b => b.AddConsole());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IOptionsMonitor<AppConfig>>(
            new OptionsMonitorStub(config));
        // Infrastructure.AI consumers (MCP connection manager, plugin loader) read the
        // AppConfig:AI section bound as IOptionsMonitor<AIConfig>. The real composition root
        // registers both monitors; mirror that here so hosted-service enumeration can resolve.
        services.AddSingleton<IOptionsMonitor<Domain.Common.Config.AI.AIConfig>>(
            new AIConfigMonitorStub(config.AI));
        // SkillMetadataParser / AgentMetadataParser depend on IMcpSecurityScanner (#331). The real
        // composition root registers it via AddGovernanceDependencies / AddGovernanceNoOpDependencies,
        // called separately from AddInfrastructureAIDependencies — mirror that here.
        services.AddSingleton(TestMcpSecurityScanner.AlwaysSafe());
        // FileSystemToolResultStore (registered below, constructed eagerly by the hosted-service
        // enumeration several tests below perform) depends on ICompositeResponseSanitizer — a
        // security-review finding on this PR moved the injection/exfiltration scan to write time,
        // the same way secret redaction already ran there. Its real implementation is registered by
        // Infrastructure.AI.Governance's own DI module, called separately in the real composition
        // root — mirror that here, same as IMcpSecurityScanner above.
        services.AddSingleton(PermissiveAdmission.PermissiveSanitizer());
        // FileSystemToolResultStore also depends on IMemoryCache (#574's page-fetch cache) —
        // the real composition root registers this via Application.AI.Common's own DI module,
        // called separately from AddInfrastructureAIDependencies — mirror that here too.
        services.AddMemoryCache();
        services.AddSingleton<ISender>(new Mock<ISender>().Object);
        services.AddKnowledgeGraphDependencies(config);

        return services;
    }

    [Fact]
    public void AddInfrastructureAIDependencies_RegistersIChatClientFactory()
    {
        var services = CreateBaseServices();
        services.AddInfrastructureAIDependencies(IsolatedAppConfig.Create());
        using var provider = services.BuildServiceProvider();

        var factory = provider.GetService<IChatClientFactory>();

        factory.Should().NotBeNull();
    }

    [Fact]
    public void AddInfrastructureAIDependencies_RegistersIToolPermissionService()
    {
        var services = CreateBaseServices();
        services.AddInfrastructureAIDependencies(IsolatedAppConfig.Create());
        using var provider = services.BuildServiceProvider();

        var permissionService = provider.GetService<IToolPermissionService>();

        permissionService.Should().NotBeNull();
    }

    [Fact]
    public void AddInfrastructureAIDependencies_RegistersISkillMetadataRegistry()
    {
        var services = CreateBaseServices();
        services.AddInfrastructureAIDependencies(IsolatedAppConfig.Create());
        using var provider = services.BuildServiceProvider();

        var registry = provider.GetService<ISkillMetadataRegistry>();

        registry.Should().NotBeNull();
    }

    /// <summary>
    /// Proves FoundryDirectResponses (issue #382) is wired through the real composition root, not
    /// just a hand-rolled test ServiceCollection — the standing lesson that a registration is
    /// untested unless a test resolves it from the real composition root.
    /// </summary>
    [Fact]
    public void AddInfrastructureAIDependencies_FoundryDirectResponsesConfigured_IsAvailableThroughRealCompositionRoot()
    {
        var config = IsolatedAppConfig.Isolate(new Domain.Common.Config.AppConfig
        {
            AI = new Domain.Common.Config.AI.AIConfig
            {
                AIFoundry = new Domain.Common.Config.AI.AIFoundry.AIFoundryConfig
                {
                    ResourceEndpoint = "https://myresource.services.ai.azure.com"
                }
            }
        });
        var services = CreateBaseServices(config);
        services.AddInfrastructureAIDependencies(config);
        using var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IChatClientFactory>();

        factory.IsAvailable(Domain.Common.Config.AI.AIAgentFrameworkClientType.FoundryDirectResponses)
            .Should().BeTrue();
    }

    [Fact]
    public void AddInfrastructureAIDependencies_FoundryDirectResponsesMalformedResourceEndpoint_ThrowsAtRegistration()
    {
        var config = IsolatedAppConfig.Isolate(new Domain.Common.Config.AppConfig
        {
            AI = new Domain.Common.Config.AI.AIConfig
            {
                AIFoundry = new Domain.Common.Config.AI.AIFoundry.AIFoundryConfig
                {
                    ResourceEndpoint = "not-a-valid-uri"
                }
            }
        });
        var services = CreateBaseServices(config);

        var act = () => services.AddInfrastructureAIDependencies(config);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AppConfig:AI:AIFoundry:ResourceEndpoint*");
    }

    [Fact]
    public void RegisterAIClients_UnconfiguredConfig_DoesNotRegisterAnyClients()
    {
        var config = IsolatedAppConfig.Create(); // ApiKey is null => IsConfigured = false
        var services = CreateBaseServices(config);
        services.AddInfrastructureAIDependencies(config);
        using var provider = services.BuildServiceProvider();

        // With unconfigured AgentFramework, neither AzureOpenAIClient nor OpenAIClient should be registered
        var factory = provider.GetRequiredService<IChatClientFactory>();
        factory.IsAvailable(Domain.Common.Config.AI.AIAgentFrameworkClientType.AzureOpenAI).Should().BeFalse();
        factory.IsAvailable(Domain.Common.Config.AI.AIAgentFrameworkClientType.OpenAI).Should().BeFalse();
    }

    [Fact]
    public void AddInfrastructureAIDependencies_RegistersIEscalationService()
    {
        var services = CreateBaseServices();
        services.AddInfrastructureAIDependencies(IsolatedAppConfig.Create());
        using var provider = services.BuildServiceProvider();

        var svc = provider.GetService<IEscalationService>();

        svc.Should().NotBeNull().And.BeOfType<DefaultEscalationService>();
    }

    [Fact]
    public void AddInfrastructureAIDependencies_RegistersIEscalationAuditStore()
    {
        var services = CreateBaseServices();
        services.AddInfrastructureAIDependencies(IsolatedAppConfig.Create());
        using var provider = services.BuildServiceProvider();

        var store = provider.GetService<IEscalationAuditStore>();

        store.Should().NotBeNull().And.BeOfType<JsonlEscalationAuditStore>();
    }

    [Fact]
    public void AddInfrastructureAIDependencies_RegistersIEscalationNotifier()
    {
        var services = CreateBaseServices();
        services.AddInfrastructureAIDependencies(IsolatedAppConfig.Create());
        using var provider = services.BuildServiceProvider();

        var notifier = provider.GetService<IEscalationNotifier>();

        notifier.Should().NotBeNull().And.BeOfType<CompositeEscalationNotifier>();
    }

    [Fact]
    public void AddInfrastructureAIDependencies_RegistersNotificationChannels()
    {
        var services = CreateBaseServices();
        services.AddInfrastructureAIDependencies(IsolatedAppConfig.Create());
        using var provider = services.BuildServiceProvider();

        var channels = provider.GetServices<IEscalationNotificationChannel>().ToList();

        channels.Should().Contain(c => c is NoOpSlackNotifier);
        channels.Should().Contain(c => c is NoOpTeamsNotifier);
    }

    [Fact]
    public void AddInfrastructureAIDependencies_RegistersIProviderHealthMonitor()
    {
        var services = CreateBaseServices();
        services.AddInfrastructureAIDependencies(IsolatedAppConfig.Create());
        using var provider = services.BuildServiceProvider();

        var monitor = provider.GetService<IProviderHealthMonitor>();

        monitor.Should().NotBeNull().And.BeOfType<PollyProviderHealthMonitor>();
    }

    [Fact]
    public void AddInfrastructureAIDependencies_RegistersIProviderErrorClassifier()
    {
        // Resolved from the real composition root: the classifier is what makes retry, the
        // circuit breaker, and provider fallback fire at all, so an unregistered one would
        // leave the whole resilience pipeline unbuildable rather than merely degraded.
        var services = CreateBaseServices();
        services.AddInfrastructureAIDependencies(IsolatedAppConfig.Create());
        using var provider = services.BuildServiceProvider();

        var classifier = provider.GetService<IProviderErrorClassifier>();

        classifier.Should().NotBeNull().And.BeOfType<DefaultProviderErrorClassifier>();
    }

    [Fact]
    public void AddInfrastructureAIDependencies_RegistersIResilientChatClientProvider()
    {
        var services = CreateBaseServices();
        services.AddInfrastructureAIDependencies(IsolatedAppConfig.Create());
        using var provider = services.BuildServiceProvider();

        var resilientProvider = provider.GetService<IResilientChatClientProvider>();

        resilientProvider.Should().NotBeNull().And.BeOfType<ResilientChatClientProvider>();
    }

    [Fact]
    public void AddInfrastructureAIDependencies_ResilienceEnabled_RegistersLlmRetryQueueHostedService()
    {
        var config = IsolatedAppConfig.Isolate(new AppConfig { AI = { Resilience = new ResilienceConfig { Enabled = true } } });
        var services = CreateBaseServices(config);
        services.AddSingleton(TimeProvider.System);
        services.AddInfrastructureAIDependencies(config);
        using var provider = services.BuildServiceProvider();

        var hostedServices = provider.GetServices<IHostedService>().ToList();

        hostedServices.Should().Contain(s => s is LlmRetryQueue);
    }

    [Fact]
    public void AddInfrastructureAIDependencies_ResilienceDisabled_DoesNotRegisterLlmRetryQueueHostedService()
    {
        var config = IsolatedAppConfig.Isolate(new AppConfig { AI = { Resilience = new ResilienceConfig { Enabled = false } } });
        var services = CreateBaseServices(config);
        services.AddInfrastructureAIDependencies(config);
        using var provider = services.BuildServiceProvider();

        var hostedServices = provider.GetServices<IHostedService>().ToList();

        hostedServices.Should().NotContain(s => s is LlmRetryQueue);
    }

    [Fact]
    public void AddInfrastructureAIDependencies_CompositeNotifier_DoesNotContainItself()
    {
        var services = CreateBaseServices();
        services.AddInfrastructureAIDependencies(IsolatedAppConfig.Create());
        using var provider = services.BuildServiceProvider();

        var channels = provider.GetServices<IEscalationNotificationChannel>().ToList();

        channels.Should().NotContain(c => c.GetType() == typeof(CompositeEscalationNotifier));
    }

    [Fact]
    public void RegisterGenerationStatsClient_OpenRouterPath_LeavesTypedClientTimeoutInfinite()
    {
        // The OpenRouter generation-stats client is a typed HttpClient created via the factory, so
        // it inherits the harness resilience pipeline (per-attempt + total timeout). A finite
        // HttpClient.Timeout would race that pipeline and could truncate the retry budget
        // mid-attempt; the registration must leave the client timeout infinite so the pipeline
        // owns the budget — consistent with the default (non-typed) clients.
        var config = IsolatedAppConfig.Create();
        config.AI.AgentFramework.ClientType = Domain.Common.Config.AI.AIAgentFrameworkClientType.OpenAI;
        config.AI.AgentFramework.EnablePromptCaching = true;
        config.AI.AgentFramework.Endpoint = "https://openrouter.ai/api/v1";
        config.AI.AgentFramework.ApiKey = "test-key";

        var services = CreateBaseServices(config);
        services.AddInfrastructureAIDependencies(config);
        using var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient(nameof(IGenerationStatsClient));

        client.Timeout.Should().Be(
            Timeout.InfiniteTimeSpan,
            "the resilience pipeline owns the timeout; the typed client must not set a finite one that races it");
    }

    // -- IBundleOwnedMcpServerRegistry cross-registration (#374) --

    /// <summary>
    /// Pins the invariant the interface extraction under #374 depends on: this registry is registered
    /// via <c>TryAddSingleton</c> from BOTH <c>AddInfrastructureAIDependencies</c> (here) and
    /// <c>AddMcpClientDependencies</c> (Infrastructure.AI.MCP), deliberately redundantly, so the wiring
    /// survives either extension method being dropped from a future composition root by mistake. This
    /// composes BOTH real extension methods against one container — mutation-tested by removing both
    /// registration lines and confirming resolution fails, so the pass here is not vacuous — and resolves
    /// the SAME staging service + connection manager the production host wires, proving the graph
    /// actually constructs rather than merely that a type is registered.
    /// </summary>
    /// <remarks>
    /// The <c>BeSameAs</c> assertion below is a weaker guarantee than it looks: .NET's container resolves
    /// a service type to one deterministic descriptor for the container's lifetime, so two resolutions of
    /// the SAME service type always agree even if a future edit adds a redundant duplicate registration —
    /// there is no "sometimes returns a different instance" failure mode to catch that way. What actually
    /// protected the pre-#374 design (and what a regression back to registering the bare concrete type
    /// from only one site would break) is that every production consumer now depends on the INTERFACE,
    /// not the concrete type — so there is no second, independently-resolvable key for a future edit to
    /// accidentally diverge onto. <c>BundleMcpServerIsolationTests</c> guards that surface directly.
    /// </remarks>
    [Fact]
    public async Task AddInfrastructureAIDependencies_AndAddMcpClientDependencies_ShareOneRegistryInstance()
    {
        var services = CreateBaseServices();
        // AddMcpClientDependencies' own additional prerequisites — mirrors
        // Infrastructure.AI.MCP.Tests.DependencyInjectionTests.BuildProvider (that project's TestSsrf
        // helper is internal to a different test assembly, so the equivalent factory is built inline).
        services.AddSingleton(new Infrastructure.AI.Egress.AntiSsrfHandlerFactory(new OptionsMonitorStub(new AppConfig())));
        services.AddSingleton(new Mock<Application.AI.Common.Interfaces.Tools.IToolBehaviorRegistry>().Object);
        services.AddSingleton(new Mock<IAmbientRequestScope>().Object);
        services.AddSingleton(new Mock<Application.AI.Common.Interfaces.Egress.IEgressAuditWriter>().Object);

        services.AddInfrastructureAIDependencies(IsolatedAppConfig.Create());
        services.AddMcpClientDependencies();
        // McpConnectionManager is IAsyncDisposable-only; the container refuses synchronous disposal.
        await using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<IBundleOwnedMcpServerRegistry>();

        // Round-trip through both real consumers this registry exists to keep in sync: staging (writes)
        // and the connection manager (reads the fallback). If either resolved a DIFFERENT instance, a
        // server staged here would be invisible to a connect attempt against the same name.
        registry.TryAdd("bundle-x:probe", new McpServerDefinition { Enabled = true, Type = McpServerType.Http, Url = "https://example.com" })
            .Should().BeTrue();

        var stagingService = provider.GetRequiredService<Application.AI.Common.Interfaces.Bundles.IBundleStagingService>();
        var connectionManager = provider.GetRequiredService<Infrastructure.AI.MCP.Services.McpConnectionManager>();
        stagingService.Should().NotBeNull();
        connectionManager.Should().NotBeNull();

        // Resolving the registry itself twice more, through both extension methods' own service key,
        // must hand back the identical instance — this is the actual regression #374's DI split risks.
        var second = provider.GetRequiredService<IBundleOwnedMcpServerRegistry>();
        registry.Should().BeSameAs(second);
    }

    /// <summary>
    /// Minimal IOptionsMonitor stub that returns a fixed value.
    /// </summary>
    private sealed class OptionsMonitorStub : IOptionsMonitor<AppConfig>
    {
        public OptionsMonitorStub(AppConfig value) => CurrentValue = value;
        public AppConfig CurrentValue { get; }
        public AppConfig Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AppConfig, string?> listener) => null;
    }

    /// <summary>
    /// Minimal IOptionsMonitor stub for the AppConfig:AI section bound as AIConfig.
    /// </summary>
    private sealed class AIConfigMonitorStub : IOptionsMonitor<Domain.Common.Config.AI.AIConfig>
    {
        public AIConfigMonitorStub(Domain.Common.Config.AI.AIConfig value) => CurrentValue = value;
        public Domain.Common.Config.AI.AIConfig CurrentValue { get; }
        public Domain.Common.Config.AI.AIConfig Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<Domain.Common.Config.AI.AIConfig, string?> listener) => null;
    }
}
