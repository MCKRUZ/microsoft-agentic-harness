diff --git a/src/Content/Application/Application.AI.Common/Factories/AgentExecutionContextFactory.cs b/src/Content/Application/Application.AI.Common/Factories/AgentExecutionContextFactory.cs
index 39d9459..915461e 100644
--- a/src/Content/Application/Application.AI.Common/Factories/AgentExecutionContextFactory.cs
+++ b/src/Content/Application/Application.AI.Common/Factories/AgentExecutionContextFactory.cs
@@ -1,6 +1,7 @@
 using Application.AI.Common.Helpers;
 using Application.AI.Common.Interfaces;
 using Application.AI.Common.Interfaces.Context;
+using Application.AI.Common.Interfaces.Resilience;
 using Application.AI.Common.Interfaces.Skills;
 using Application.AI.Common.Interfaces.Tools;
 using Application.AI.Common.Interfaces.Traces;
@@ -40,6 +41,7 @@ public class AgentExecutionContextFactory
     private readonly IExecutionTraceStore? _traceStore;
     private readonly ISkillContentProvider? _skillContentProvider;
     private readonly IAgentConfigReporter? _agentConfigReporter;
+    private readonly IResilientChatClientProvider? _resilientChatClientProvider;
 
     public AgentExecutionContextFactory(
         ILogger<AgentExecutionContextFactory> logger,
@@ -51,7 +53,8 @@ public class AgentExecutionContextFactory
         IContextBudgetTracker? budgetTracker = null,
         IExecutionTraceStore? traceStore = null,
         ISkillContentProvider? skillContentProvider = null,
-        IAgentConfigReporter? agentConfigReporter = null)
+        IAgentConfigReporter? agentConfigReporter = null,
+        IResilientChatClientProvider? resilientChatClientProvider = null)
     {
         _logger = logger;
         _appConfig = appConfig;
@@ -63,6 +66,7 @@ public class AgentExecutionContextFactory
         _traceStore = traceStore;
         _skillContentProvider = skillContentProvider;
         _agentConfigReporter = agentConfigReporter;
+        _resilientChatClientProvider = resilientChatClientProvider;
     }
 
     /// <summary>
@@ -118,6 +122,13 @@ public class AgentExecutionContextFactory
         if (_skillContentProvider != null)
             additionalProps[ISkillContentProvider.AdditionalPropertiesKey] = _skillContentProvider;
 
+        // Stash resilient chat client for transparent fallback when resilience is enabled
+        if (_resilientChatClientProvider is not null)
+        {
+            var resilientClient = await _resilientChatClientProvider.GetResilientChatClientAsync();
+            additionalProps["__resilientChatClient"] = resilientClient;
+        }
+
         // Start a trace run when a store is wired in
         if (_traceStore != null)
         {
diff --git a/src/Content/Infrastructure/Infrastructure.AI/Resilience/ProviderResiliencePipelineBuilder.cs b/src/Content/Infrastructure/Infrastructure.AI/Resilience/ProviderResiliencePipelineBuilder.cs
index dd00ad2..3778866 100644
--- a/src/Content/Infrastructure/Infrastructure.AI/Resilience/ProviderResiliencePipelineBuilder.cs
+++ b/src/Content/Infrastructure/Infrastructure.AI/Resilience/ProviderResiliencePipelineBuilder.cs
@@ -1,4 +1,5 @@
 using Application.AI.Common.OpenTelemetry.Metrics;
+using Domain.AI.Resilience;
 using Domain.AI.Telemetry.Conventions;
 using Domain.Common.Config.AI.Resilience;
 using Microsoft.Extensions.AI;
@@ -38,12 +39,14 @@ public static class ProviderResiliencePipelineBuilder
     /// <param name="providerName">Logical name for this provider (used in OTel tags and circuit breaker isolation).</param>
     /// <param name="config">Resilience configuration from Options pattern.</param>
     /// <param name="circuitBreakerStateProvider">Output: the Polly state provider for this pipeline, used by health monitor to query circuit state.</param>
+    /// <param name="onCircuitStateChanged">Optional callback invoked on circuit state transitions (Opened→Unavailable, Closed→Healthy, HalfOpened→Degraded).</param>
     /// <param name="logger">Logger for retry/circuit events.</param>
     /// <returns>A configured resilience pipeline scoped to this provider.</returns>
     public static ResiliencePipeline<ChatResponse> Build(
         string providerName,
         ResilienceConfig config,
         out CircuitBreakerStateProvider circuitBreakerStateProvider,
+        Action<ProviderHealthState>? onCircuitStateChanged = null,
         ILogger? logger = null)
     {
         var stateProvider = new CircuitBreakerStateProvider();
@@ -51,7 +54,7 @@ public static class ProviderResiliencePipelineBuilder
 
         var pipeline = new ResiliencePipelineBuilder<ChatResponse>()
             .AddRetry(CreateRetryOptions(providerName, config.Retry, logger))
-            .AddCircuitBreaker(CreateCircuitBreakerOptions(providerName, config.CircuitBreaker, stateProvider, logger))
+            .AddCircuitBreaker(CreateCircuitBreakerOptions(providerName, config.CircuitBreaker, stateProvider, onCircuitStateChanged, logger))
             .AddTimeout(CreateTimeoutOptions(config.Timeout))
             .Build();
 
@@ -73,12 +76,14 @@ public static class ProviderResiliencePipelineBuilder
     /// <param name="providerName">Logical name for this provider.</param>
     /// <param name="config">Resilience configuration.</param>
     /// <param name="sharedStateProvider">State provider for read-only health queries. Does not synchronize circuit state across pipelines.</param>
+    /// <param name="onCircuitStateChanged">Optional callback invoked on circuit state transitions.</param>
     /// <param name="logger">Logger for retry/circuit events.</param>
     /// <returns>A non-generic resilience pipeline for stream initiation.</returns>
     public static ResiliencePipeline BuildForStreamInitiation(
         string providerName,
         ResilienceConfig config,
         CircuitBreakerStateProvider sharedStateProvider,
+        Action<ProviderHealthState>? onCircuitStateChanged = null,
         ILogger? logger = null)
     {
         var pipeline = new ResiliencePipelineBuilder()
@@ -106,11 +111,19 @@ public static class ProviderResiliencePipelineBuilder
                 OnOpened = args =>
                 {
                     RecordCircuitOpened(providerName, logger);
+                    onCircuitStateChanged?.Invoke(ProviderHealthState.Unavailable);
                     return default;
                 },
                 OnClosed = args =>
                 {
                     RecordCircuitClosed(providerName, logger);
+                    onCircuitStateChanged?.Invoke(ProviderHealthState.Healthy);
+                    return default;
+                },
+                OnHalfOpened = args =>
+                {
+                    logger?.LogInformation("Stream circuit half-opened for provider {Provider}", providerName);
+                    onCircuitStateChanged?.Invoke(ProviderHealthState.Degraded);
                     return default;
                 }
             })
@@ -142,7 +155,7 @@ public static class ProviderResiliencePipelineBuilder
     }
 
     private static CircuitBreakerStrategyOptions<ChatResponse> CreateCircuitBreakerOptions(
-        string providerName, CircuitBreakerConfig cbConfig, CircuitBreakerStateProvider stateProvider, ILogger? logger)
+        string providerName, CircuitBreakerConfig cbConfig, CircuitBreakerStateProvider stateProvider, Action<ProviderHealthState>? onCircuitStateChanged, ILogger? logger)
     {
         return new CircuitBreakerStrategyOptions<ChatResponse>
         {
@@ -158,16 +171,19 @@ public static class ProviderResiliencePipelineBuilder
             OnOpened = args =>
             {
                 RecordCircuitOpened(providerName, logger);
+                onCircuitStateChanged?.Invoke(ProviderHealthState.Unavailable);
                 return default;
             },
             OnClosed = args =>
             {
                 RecordCircuitClosed(providerName, logger);
+                onCircuitStateChanged?.Invoke(ProviderHealthState.Healthy);
                 return default;
             },
             OnHalfOpened = args =>
             {
                 logger?.LogInformation("Circuit half-opened for provider {Provider}", providerName);
+                onCircuitStateChanged?.Invoke(ProviderHealthState.Degraded);
                 return default;
             }
         };
diff --git a/src/Content/Infrastructure/Infrastructure.AI/Resilience/ResilientChatClientProvider.cs b/src/Content/Infrastructure/Infrastructure.AI/Resilience/ResilientChatClientProvider.cs
new file mode 100644
index 0000000..abe7f80
--- /dev/null
+++ b/src/Content/Infrastructure/Infrastructure.AI/Resilience/ResilientChatClientProvider.cs
@@ -0,0 +1,120 @@
+using Application.AI.Common.Interfaces;
+using Application.AI.Common.Interfaces.Resilience;
+using Domain.AI.Resilience;
+using Domain.Common.Config.AI.Resilience;
+using Microsoft.Extensions.AI;
+using Microsoft.Extensions.Logging;
+using Microsoft.Extensions.Options;
+using Polly.CircuitBreaker;
+
+namespace Infrastructure.AI.Resilience;
+
+/// <summary>
+/// Composes a <see cref="ResilientChatClient"/> from the configured fallback chain.
+/// Reads provider entries from <see cref="ResilienceConfig.FallbackChain"/>, creates
+/// raw <see cref="IChatClient"/> instances via <see cref="IChatClientFactory"/>,
+/// wraps each in a per-provider Polly resilience pipeline, and caches the result.
+/// </summary>
+/// <remarks>
+/// <para>
+/// When <see cref="ResilienceConfig.Enabled"/> is false, returns the primary provider's
+/// raw client directly — no Polly wrapping, no fallback chain, no overhead.
+/// </para>
+/// <para>
+/// The composed client is cached (lazy-initialized). The fallback chain is static for
+/// the process lifetime. Config changes require restart.
+/// </para>
+/// </remarks>
+public sealed class ResilientChatClientProvider : IResilientChatClientProvider
+{
+    private readonly IChatClientFactory _chatClientFactory;
+    private readonly IOptionsMonitor<ResilienceConfig> _resilienceConfig;
+    private readonly PollyProviderHealthMonitor _healthMonitor;
+    private readonly ProviderCapabilityRegistry _capabilityRegistry;
+    private readonly ILogger<ResilientChatClientProvider> _logger;
+    private readonly Lazy<Task<IChatClient>> _cachedClient;
+
+    /// <summary>Creates a new resilient chat client provider.</summary>
+    /// <param name="chatClientFactory">Factory for creating raw per-provider clients.</param>
+    /// <param name="resilienceConfig">Resilience configuration from Options pattern.</param>
+    /// <param name="healthMonitor">Concrete health monitor for <see cref="PollyProviderHealthMonitor.ReportStateChange"/> wiring.</param>
+    /// <param name="capabilityRegistry">Provider capability registry for capability diffing.</param>
+    /// <param name="logger">Logger for chain composition events.</param>
+    public ResilientChatClientProvider(
+        IChatClientFactory chatClientFactory,
+        IOptionsMonitor<ResilienceConfig> resilienceConfig,
+        PollyProviderHealthMonitor healthMonitor,
+        ProviderCapabilityRegistry capabilityRegistry,
+        ILogger<ResilientChatClientProvider> logger)
+    {
+        _chatClientFactory = chatClientFactory;
+        _resilienceConfig = resilienceConfig;
+        _healthMonitor = healthMonitor;
+        _capabilityRegistry = capabilityRegistry;
+        _logger = logger;
+        _cachedClient = new Lazy<Task<IChatClient>>(ComposeChainAsync);
+    }
+
+    /// <inheritdoc/>
+    public Task<IChatClient> GetResilientChatClientAsync(CancellationToken ct = default)
+    {
+        return _cachedClient.Value;
+    }
+
+    private async Task<IChatClient> ComposeChainAsync()
+    {
+        var config = _resilienceConfig.CurrentValue;
+        var chain = config.FallbackChain;
+
+        if (chain.Length == 0)
+            throw new InvalidOperationException(
+                "ResilienceConfig.FallbackChain is empty. Configure at least one provider.");
+
+        if (!config.Enabled)
+        {
+            var primary = chain[0];
+            _logger.LogWarning("Resilience disabled — returning raw client for {DeploymentId}", primary.DeploymentId);
+            return await _chatClientFactory.GetChatClientAsync(
+                primary.ClientType, primary.DeploymentId);
+        }
+
+        var providers = new List<ResilientChatClient.ProviderEntry>(chain.Length);
+
+        foreach (var entry in chain)
+        {
+            var rawClient = await _chatClientFactory.GetChatClientAsync(
+                entry.ClientType, entry.DeploymentId);
+
+            var pipeline = ProviderResiliencePipelineBuilder.Build(
+                providerName: entry.DeploymentId,
+                config: config,
+                out var stateProvider,
+                onCircuitStateChanged: newState =>
+                    _healthMonitor.ReportStateChange(entry.DeploymentId, newState),
+                logger: _logger);
+
+            // Polly v8 CircuitBreakerStateProvider can only bind to one circuit breaker strategy,
+            // so the stream pipeline needs its own instance.
+            var streamStateProvider = new CircuitBreakerStateProvider();
+            var streamPipeline = ProviderResiliencePipelineBuilder.BuildForStreamInitiation(
+                providerName: entry.DeploymentId,
+                config: config,
+                sharedStateProvider: streamStateProvider,
+                onCircuitStateChanged: newState =>
+                    _healthMonitor.ReportStateChange(entry.DeploymentId, newState),
+                logger: _logger);
+
+            providers.Add(new ResilientChatClient.ProviderEntry(
+                entry.DeploymentId, rawClient, pipeline, streamPipeline));
+
+            _logger.LogDebug("Created provider entry {DeploymentId} ({ClientType})",
+                entry.DeploymentId, entry.ClientType);
+        }
+
+        var providerNames = string.Join(", ", chain.Select(c => c.DeploymentId));
+        _logger.LogInformation("Composed resilient chat client with {Count} providers: {ProviderNames}",
+            providers.Count, providerNames);
+
+        return new ResilientChatClient(providers, _healthMonitor, _logger as ILogger<ResilientChatClient>);
+    }
+}
diff --git a/src/Content/Tests/Infrastructure.AI.Tests/Resilience/ResilientChatClientProviderTests.cs b/src/Content/Tests/Infrastructure.AI.Tests/Resilience/ResilientChatClientProviderTests.cs
new file mode 100644
index 0000000..b6df84d
--- /dev/null
+++ b/src/Content/Tests/Infrastructure.AI.Tests/Resilience/ResilientChatClientProviderTests.cs
@@ -0,0 +1,189 @@
+using Application.AI.Common.Interfaces;
+using Application.AI.Common.Interfaces.Resilience;
+using Domain.Common.Config.AI;
+using Domain.Common.Config.AI.Resilience;
+using FluentAssertions;
+using Infrastructure.AI.Resilience;
+using Microsoft.Extensions.AI;
+using Microsoft.Extensions.Logging;
+using Microsoft.Extensions.Logging.Abstractions;
+using Microsoft.Extensions.Options;
+using Moq;
+using Xunit;
+
+namespace Infrastructure.AI.Tests.Resilience;
+
+/// <summary>
+/// Tests for <see cref="ResilientChatClientProvider"/> — the composition root that
+/// assembles the fallback chain from config, wires resilience pipelines, and returns
+/// a <see cref="ResilientChatClient"/>. Validates config-driven composition, caching,
+/// and the disabled-resilience bypass.
+/// </summary>
+public sealed class ResilientChatClientProviderTests : IAsyncDisposable
+{
+    private readonly Mock<IChatClientFactory> _chatClientFactory = new();
+    private readonly PollyProviderHealthMonitor _healthMonitor = new(null);
+    private readonly ILogger<ResilientChatClientProvider> _logger =
+        NullLoggerFactory.Instance.CreateLogger<ResilientChatClientProvider>();
+
+    private readonly List<IChatClient> _createdClients = [];
+
+    public async ValueTask DisposeAsync()
+    {
+        foreach (var client in _createdClients)
+            client.Dispose();
+    }
+
+    [Fact]
+    public async Task GetResilientChatClientAsync_BuildsChainFromConfig()
+    {
+        var config = CreateEnabledConfig(
+            ("AzureOpenAI", "gpt-4o"),
+            ("AzureOpenAI", "gpt-35-turbo"));
+
+        SetupFactoryDefaults();
+        var sut = CreateProvider(config);
+
+        var client = await sut.GetResilientChatClientAsync();
+
+        client.Should().NotBeNull();
+        client.Should().BeOfType<ResilientChatClient>();
+        _chatClientFactory.Verify(
+            f => f.GetChatClientAsync(AIAgentFrameworkClientType.AzureOpenAI, "gpt-4o", It.IsAny<CancellationToken>()),
+            Times.Once);
+        _chatClientFactory.Verify(
+            f => f.GetChatClientAsync(AIAgentFrameworkClientType.AzureOpenAI, "gpt-35-turbo", It.IsAny<CancellationToken>()),
+            Times.Once);
+    }
+
+    [Fact]
+    public async Task GetResilientChatClientAsync_CachesResult()
+    {
+        var config = CreateEnabledConfig(("AzureOpenAI", "gpt-4o"));
+        SetupFactoryDefaults();
+        var sut = CreateProvider(config);
+
+        var first = await sut.GetResilientChatClientAsync();
+        var second = await sut.GetResilientChatClientAsync();
+
+        ReferenceEquals(first, second).Should().BeTrue();
+        _chatClientFactory.Verify(
+            f => f.GetChatClientAsync(It.IsAny<AIAgentFrameworkClientType>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
+            Times.Once);
+    }
+
+    [Fact]
+    public async Task GetResilientChatClientAsync_EmptyChain_ThrowsInvalidOperation()
+    {
+        var config = new ResilienceConfig { Enabled = true, FallbackChain = [] };
+        var sut = CreateProvider(config);
+
+        var act = () => sut.GetResilientChatClientAsync();
+
+        await act.Should().ThrowAsync<InvalidOperationException>()
+            .WithMessage("*FallbackChain*empty*");
+    }
+
+    [Fact]
+    public async Task GetResilientChatClientAsync_FactoryFailure_PropagatesException()
+    {
+        var config = CreateEnabledConfig(("AzureOpenAI", "gpt-4o"));
+        _chatClientFactory
+            .Setup(f => f.GetChatClientAsync(It.IsAny<AIAgentFrameworkClientType>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
+            .ThrowsAsync(new InvalidOperationException("Provider not configured"));
+
+        var sut = CreateProvider(config);
+
+        var act = () => sut.GetResilientChatClientAsync();
+
+        await act.Should().ThrowAsync<InvalidOperationException>()
+            .WithMessage("Provider not configured");
+    }
+
+    [Fact]
+    public async Task GetResilientChatClientAsync_Disabled_ReturnsRawClient()
+    {
+        var rawClient = CreateMockChatClient();
+        _chatClientFactory
+            .Setup(f => f.GetChatClientAsync(AIAgentFrameworkClientType.AzureOpenAI, "gpt-4o", It.IsAny<CancellationToken>()))
+            .ReturnsAsync(rawClient);
+
+        var config = new ResilienceConfig
+        {
+            Enabled = false,
+            FallbackChain =
+            [
+                new FallbackProviderConfig { ClientType = AIAgentFrameworkClientType.AzureOpenAI, DeploymentId = "gpt-4o" }
+            ]
+        };
+        var sut = CreateProvider(config);
+
+        var client = await sut.GetResilientChatClientAsync();
+
+        client.Should().BeSameAs(rawClient);
+        client.Should().NotBeOfType<ResilientChatClient>();
+    }
+
+    [Fact]
+    public async Task GetResilientChatClientAsync_Disabled_EmptyChain_ThrowsInvalidOperation()
+    {
+        var config = new ResilienceConfig { Enabled = false, FallbackChain = [] };
+        var sut = CreateProvider(config);
+
+        var act = () => sut.GetResilientChatClientAsync();
+
+        await act.Should().ThrowAsync<InvalidOperationException>()
+            .WithMessage("*FallbackChain*empty*");
+    }
+
+    private ResilientChatClientProvider CreateProvider(ResilienceConfig config)
+    {
+        var configMonitor = Mock.Of<IOptionsMonitor<ResilienceConfig>>(
+            m => m.CurrentValue == config);
+        var capabilityRegistry = new ProviderCapabilityRegistry(
+            Mock.Of<IOptionsMonitor<ResilienceConfig>>(m => m.CurrentValue == config));
+
+        return new ResilientChatClientProvider(
+            _chatClientFactory.Object,
+            configMonitor,
+            _healthMonitor,
+            capabilityRegistry,
+            _logger);
+    }
+
+    private void SetupFactoryDefaults()
+    {
+        _chatClientFactory
+            .Setup(f => f.GetChatClientAsync(It.IsAny<AIAgentFrameworkClientType>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
+            .ReturnsAsync(() => CreateMockChatClient());
+    }
+
+    private IChatClient CreateMockChatClient()
+    {
+        var mock = new Mock<IChatClient>();
+        _createdClients.Add(mock.Object);
+        return mock.Object;
+    }
+
+    private static ResilienceConfig CreateEnabledConfig(params (string clientType, string deploymentId)[] providers)
+    {
+        return new ResilienceConfig
+        {
+            Enabled = true,
+            FallbackChain = providers.Select(p => new FallbackProviderConfig
+            {
+                ClientType = Enum.Parse<AIAgentFrameworkClientType>(p.clientType),
+                DeploymentId = p.deploymentId
+            }).ToArray(),
+            Retry = new RetryConfig { MaxAttempts = 2, BaseDelaySeconds = 0.01, BackoffType = "Exponential" },
+            CircuitBreaker = new CircuitBreakerConfig
+            {
+                FailureRatio = 0.5,
+                SamplingDurationSeconds = 30,
+                MinimumThroughput = 5,
+                BreakDurationSeconds = 60
+            },
+            Timeout = new TimeoutConfig { PerAttemptSeconds = 30 }
+        };
+    }
+}
