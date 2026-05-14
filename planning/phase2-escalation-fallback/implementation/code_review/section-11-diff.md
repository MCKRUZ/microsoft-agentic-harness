diff --git a/src/Content/Infrastructure/Infrastructure.AI/Infrastructure.AI.csproj b/src/Content/Infrastructure/Infrastructure.AI/Infrastructure.AI.csproj
index 6762611..6930cc7 100644
--- a/src/Content/Infrastructure/Infrastructure.AI/Infrastructure.AI.csproj
+++ b/src/Content/Infrastructure/Infrastructure.AI/Infrastructure.AI.csproj
@@ -18,6 +18,7 @@
     <PackageReference Include="Microsoft.Extensions.Http" />
     <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
     <PackageReference Include="Microsoft.Extensions.Options" />
+    <PackageReference Include="Polly.Core" />
   </ItemGroup>
 
   <ItemGroup>
diff --git a/src/Content/Infrastructure/Infrastructure.AI/Resilience/ProviderResiliencePipelineBuilder.cs b/src/Content/Infrastructure/Infrastructure.AI/Resilience/ProviderResiliencePipelineBuilder.cs
new file mode 100644
index 0000000..907384a
--- /dev/null
+++ b/src/Content/Infrastructure/Infrastructure.AI/Resilience/ProviderResiliencePipelineBuilder.cs
@@ -0,0 +1,211 @@
+using System.Diagnostics;
+using Application.AI.Common.OpenTelemetry.Metrics;
+using Domain.AI.Telemetry.Conventions;
+using Domain.Common.Config.AI.Resilience;
+using Microsoft.Extensions.AI;
+using Microsoft.Extensions.Logging;
+using Polly;
+using Polly.CircuitBreaker;
+using Polly.Retry;
+using Polly.Timeout;
+
+namespace Infrastructure.AI.Resilience;
+
+/// <summary>
+/// Builds a per-provider resilience pipeline for LLM chat completion calls.
+/// Each provider gets independent retry, circuit breaker, and timeout strategies.
+/// </summary>
+/// <remarks>
+/// <para>
+/// Strategy composition order (outermost to innermost):
+/// <list type="number">
+///   <item><description>Retry — wraps circuit breaker and timeout</description></item>
+///   <item><description>Circuit Breaker — wraps timeout</description></item>
+///   <item><description>Timeout — per-attempt deadline (innermost)</description></item>
+/// </list>
+/// </para>
+/// <para>
+/// This means: a single attempt has a timeout. If it fails, the circuit breaker records
+/// the failure. If retries remain, the retry strategy tries again (going back through
+/// circuit breaker and timeout).
+/// </para>
+/// </remarks>
+public static class ProviderResiliencePipelineBuilder
+{
+    /// <summary>
+    /// Creates a <see cref="ResiliencePipeline{ChatResponse}"/> for the named provider
+    /// using the supplied resilience configuration.
+    /// </summary>
+    /// <param name="providerName">Logical name for this provider (used in OTel tags and circuit breaker isolation).</param>
+    /// <param name="config">Resilience configuration from Options pattern.</param>
+    /// <param name="circuitBreakerStateProvider">Output: the Polly state provider for this pipeline, used by health monitor to query circuit state.</param>
+    /// <param name="logger">Logger for retry/circuit events.</param>
+    /// <returns>A configured resilience pipeline scoped to this provider.</returns>
+    public static ResiliencePipeline<ChatResponse> Build(
+        string providerName,
+        ResilienceConfig config,
+        out CircuitBreakerStateProvider circuitBreakerStateProvider,
+        ILogger? logger = null)
+    {
+        var stateProvider = new CircuitBreakerStateProvider();
+        circuitBreakerStateProvider = stateProvider;
+
+        var pipeline = new ResiliencePipelineBuilder<ChatResponse>()
+            .AddRetry(CreateRetryOptions(providerName, config.Retry, logger))
+            .AddCircuitBreaker(CreateCircuitBreakerOptions(providerName, config.CircuitBreaker, stateProvider, logger))
+            .AddTimeout(CreateTimeoutOptions(config.Timeout))
+            .Build();
+
+        return pipeline;
+    }
+
+    /// <summary>
+    /// Builds a non-generic resilience pipeline for wrapping stream initiation.
+    /// Uses the same retry/circuit/timeout config but operates on the void-returning
+    /// initiation call rather than the stream content.
+    /// </summary>
+    /// <param name="providerName">Logical name for this provider.</param>
+    /// <param name="config">Resilience configuration.</param>
+    /// <param name="sharedStateProvider">Shared circuit breaker state provider from the typed pipeline build.</param>
+    /// <param name="logger">Logger for retry/circuit events.</param>
+    /// <returns>A non-generic resilience pipeline for stream initiation.</returns>
+    public static ResiliencePipeline BuildForStreamInitiation(
+        string providerName,
+        ResilienceConfig config,
+        CircuitBreakerStateProvider sharedStateProvider,
+        ILogger? logger = null)
+    {
+        var pipeline = new ResiliencePipelineBuilder()
+            .AddRetry(new RetryStrategyOptions
+            {
+                MaxRetryAttempts = config.Retry.MaxAttempts,
+                Delay = TimeSpan.FromSeconds(config.Retry.BaseDelaySeconds),
+                BackoffType = ParseBackoffType(config.Retry.BackoffType),
+                UseJitter = true,
+                ShouldHandle = new PredicateBuilder().Handle<HttpRequestException>().Handle<TaskCanceledException>().Handle<TimeoutRejectedException>(),
+                OnRetry = args =>
+                {
+                    RecordRetry(providerName, args.AttemptNumber, args.Outcome.Exception, logger);
+                    return default;
+                }
+            })
+            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
+            {
+                FailureRatio = config.CircuitBreaker.FailureRatio,
+                SamplingDuration = TimeSpan.FromSeconds(config.CircuitBreaker.SamplingDurationSeconds),
+                MinimumThroughput = config.CircuitBreaker.MinimumThroughput,
+                BreakDuration = TimeSpan.FromSeconds(config.CircuitBreaker.BreakDurationSeconds),
+                StateProvider = sharedStateProvider,
+                ShouldHandle = new PredicateBuilder().Handle<HttpRequestException>().Handle<TaskCanceledException>().Handle<TimeoutRejectedException>(),
+                OnOpened = args =>
+                {
+                    RecordCircuitOpened(providerName, logger);
+                    return default;
+                },
+                OnClosed = args =>
+                {
+                    RecordCircuitClosed(providerName, logger);
+                    return default;
+                }
+            })
+            .AddTimeout(TimeSpan.FromSeconds(config.Timeout.PerAttemptSeconds))
+            .Build();
+
+        return pipeline;
+    }
+
+    private static RetryStrategyOptions<ChatResponse> CreateRetryOptions(
+        string providerName, RetryConfig retryConfig, ILogger? logger)
+    {
+        return new RetryStrategyOptions<ChatResponse>
+        {
+            MaxRetryAttempts = retryConfig.MaxAttempts,
+            Delay = TimeSpan.FromSeconds(retryConfig.BaseDelaySeconds),
+            BackoffType = ParseBackoffType(retryConfig.BackoffType),
+            UseJitter = true,
+            ShouldHandle = new PredicateBuilder<ChatResponse>()
+                .Handle<HttpRequestException>()
+                .Handle<TaskCanceledException>()
+                .Handle<TimeoutRejectedException>(),
+            OnRetry = args =>
+            {
+                RecordRetry(providerName, args.AttemptNumber, args.Outcome.Exception, logger);
+                return default;
+            }
+        };
+    }
+
+    private static CircuitBreakerStrategyOptions<ChatResponse> CreateCircuitBreakerOptions(
+        string providerName, CircuitBreakerConfig cbConfig, CircuitBreakerStateProvider stateProvider, ILogger? logger)
+    {
+        return new CircuitBreakerStrategyOptions<ChatResponse>
+        {
+            FailureRatio = cbConfig.FailureRatio,
+            SamplingDuration = TimeSpan.FromSeconds(cbConfig.SamplingDurationSeconds),
+            MinimumThroughput = cbConfig.MinimumThroughput,
+            BreakDuration = TimeSpan.FromSeconds(cbConfig.BreakDurationSeconds),
+            StateProvider = stateProvider,
+            ShouldHandle = new PredicateBuilder<ChatResponse>()
+                .Handle<HttpRequestException>()
+                .Handle<TaskCanceledException>()
+                .Handle<TimeoutRejectedException>(),
+            OnOpened = args =>
+            {
+                RecordCircuitOpened(providerName, logger);
+                return default;
+            },
+            OnClosed = args =>
+            {
+                RecordCircuitClosed(providerName, logger);
+                return default;
+            },
+            OnHalfOpened = args =>
+            {
+                logger?.LogInformation("Circuit half-opened for provider {Provider}", providerName);
+                return default;
+            }
+        };
+    }
+
+    private static TimeoutStrategyOptions CreateTimeoutOptions(TimeoutConfig timeoutConfig)
+    {
+        return new TimeoutStrategyOptions
+        {
+            Timeout = TimeSpan.FromSeconds(timeoutConfig.PerAttemptSeconds)
+        };
+    }
+
+    private static DelayBackoffType ParseBackoffType(string backoffType)
+    {
+        return backoffType.Equals("Linear", StringComparison.OrdinalIgnoreCase)
+            ? DelayBackoffType.Linear
+            : DelayBackoffType.Exponential;
+    }
+
+    private static void RecordRetry(string providerName, int attemptNumber, Exception? exception, ILogger? logger)
+    {
+        ResilienceMetrics.RetryAttempts.Add(1,
+            new KeyValuePair<string, object?>(ResilienceConventions.ProviderName, providerName));
+
+        logger?.LogWarning("Retry {Attempt} for provider {Provider}: {Exception}",
+            attemptNumber, providerName, exception?.Message);
+    }
+
+    private static void RecordCircuitOpened(string providerName, ILogger? logger)
+    {
+        ResilienceMetrics.CircuitStateChanges.Add(1,
+            new KeyValuePair<string, object?>(ResilienceConventions.ProviderName, providerName),
+            new KeyValuePair<string, object?>(ResilienceConventions.TransitionTo, ResilienceConventions.HealthValues.Unavailable));
+
+        logger?.LogError("Circuit opened for provider {Provider}", providerName);
+    }
+
+    private static void RecordCircuitClosed(string providerName, ILogger? logger)
+    {
+        ResilienceMetrics.CircuitStateChanges.Add(1,
+            new KeyValuePair<string, object?>(ResilienceConventions.ProviderName, providerName),
+            new KeyValuePair<string, object?>(ResilienceConventions.TransitionTo, ResilienceConventions.HealthValues.Healthy));
+
+        logger?.LogInformation("Circuit closed for provider {Provider}", providerName);
+    }
+}
diff --git a/src/Content/Tests/Infrastructure.AI.Tests/Resilience/ProviderResiliencePipelineTests.cs b/src/Content/Tests/Infrastructure.AI.Tests/Resilience/ProviderResiliencePipelineTests.cs
new file mode 100644
index 0000000..d49b8d5
--- /dev/null
+++ b/src/Content/Tests/Infrastructure.AI.Tests/Resilience/ProviderResiliencePipelineTests.cs
@@ -0,0 +1,211 @@
+using Domain.Common.Config.AI.Resilience;
+using FluentAssertions;
+using Infrastructure.AI.Resilience;
+using Microsoft.Extensions.AI;
+using Polly.CircuitBreaker;
+using Polly.Timeout;
+using Xunit;
+
+namespace Infrastructure.AI.Tests.Resilience;
+
+/// <summary>
+/// Tests for <see cref="ProviderResiliencePipelineBuilder"/> — verifies that per-provider
+/// resilience pipelines correctly compose retry, circuit breaker, and timeout
+/// strategies with config-driven parameters.
+/// </summary>
+public sealed class ProviderResiliencePipelineTests
+{
+    [Fact]
+    public async Task Pipeline_TransientError_RetriesToConfiguredMax()
+    {
+        var config = CreateTestConfig(maxAttempts: 2);
+        var pipeline = ProviderResiliencePipelineBuilder.Build("test-provider", config, out _);
+        var callCount = 0;
+
+        var result = await pipeline.ExecuteAsync(async ct =>
+        {
+            callCount++;
+            if (callCount <= 2)
+                throw new HttpRequestException("transient");
+            return CreateSuccessResponse();
+        }, CancellationToken.None);
+
+        callCount.Should().Be(3);
+        result.Should().NotBeNull();
+    }
+
+    [Fact]
+    public async Task Pipeline_Http429_TriggersRetry()
+    {
+        var config = CreateTestConfig(maxAttempts: 2);
+        var pipeline = ProviderResiliencePipelineBuilder.Build("test-provider", config, out _);
+        var callCount = 0;
+
+        var result = await pipeline.ExecuteAsync(async ct =>
+        {
+            callCount++;
+            if (callCount == 1)
+                throw new HttpRequestException("Too Many Requests", null, System.Net.HttpStatusCode.TooManyRequests);
+            return CreateSuccessResponse();
+        }, CancellationToken.None);
+
+        callCount.Should().Be(2);
+        result.Should().NotBeNull();
+    }
+
+    [Fact]
+    public async Task Pipeline_Http500_TriggersRetry()
+    {
+        var config = CreateTestConfig(maxAttempts: 2);
+        var pipeline = ProviderResiliencePipelineBuilder.Build("test-provider", config, out _);
+        var callCount = 0;
+
+        var result = await pipeline.ExecuteAsync(async ct =>
+        {
+            callCount++;
+            if (callCount == 1)
+                throw new HttpRequestException("Internal Server Error", null, System.Net.HttpStatusCode.InternalServerError);
+            return CreateSuccessResponse();
+        }, CancellationToken.None);
+
+        callCount.Should().Be(2);
+        result.Should().NotBeNull();
+    }
+
+    [Fact]
+    public async Task Pipeline_FailureRatioExceeded_OpensCircuit()
+    {
+        var config = CreateTestConfig(maxAttempts: 1, failureRatio: 0.5, minimumThroughput: 2, samplingDurationSeconds: 30);
+        var pipeline = ProviderResiliencePipelineBuilder.Build("test-provider", config, out var stateProvider);
+
+        for (var i = 0; i < 4; i++)
+        {
+            try
+            {
+                await pipeline.ExecuteAsync<ChatResponse>(async ct =>
+                    throw new HttpRequestException("fail"), CancellationToken.None);
+            }
+            catch { }
+        }
+
+        var act = async () => await pipeline.ExecuteAsync(async ct => CreateSuccessResponse(), CancellationToken.None);
+        await act.Should().ThrowAsync<BrokenCircuitException>();
+    }
+
+    [Fact]
+    public async Task Pipeline_CircuitOpen_ThrowsBrokenCircuitExceptionWithoutInvokingDelegate()
+    {
+        var config = CreateTestConfig(maxAttempts: 1, failureRatio: 0.5, minimumThroughput: 2, samplingDurationSeconds: 30);
+        var pipeline = ProviderResiliencePipelineBuilder.Build("test-provider", config, out _);
+
+        for (var i = 0; i < 4; i++)
+        {
+            try
+            {
+                await pipeline.ExecuteAsync<ChatResponse>(async ct =>
+                    throw new HttpRequestException("fail"), CancellationToken.None);
+            }
+            catch { }
+        }
+
+        var delegateCalled = false;
+        var act = async () => await pipeline.ExecuteAsync(async ct =>
+        {
+            delegateCalled = true;
+            return CreateSuccessResponse();
+        }, CancellationToken.None);
+
+        await act.Should().ThrowAsync<BrokenCircuitException>();
+        delegateCalled.Should().BeFalse();
+    }
+
+    [Fact]
+    public async Task Pipeline_SuccessAfterRetry_CircuitRemainsClosed()
+    {
+        var config = CreateTestConfig(maxAttempts: 2, failureRatio: 0.5, minimumThroughput: 4, samplingDurationSeconds: 30);
+        var pipeline = ProviderResiliencePipelineBuilder.Build("test-provider", config, out var stateProvider);
+        var callCount = 0;
+
+        await pipeline.ExecuteAsync(async ct =>
+        {
+            callCount++;
+            if (callCount == 1)
+                throw new HttpRequestException("transient");
+            return CreateSuccessResponse();
+        }, CancellationToken.None);
+
+        var result = await pipeline.ExecuteAsync(async ct => CreateSuccessResponse(), CancellationToken.None);
+
+        result.Should().NotBeNull();
+        stateProvider.CircuitState.Should().Be(CircuitState.Closed);
+    }
+
+    [Fact]
+    public async Task Pipeline_Timeout_CancelsAttempt()
+    {
+        var config = CreateTestConfig(maxAttempts: 1, perAttemptSeconds: 1);
+        var pipeline = ProviderResiliencePipelineBuilder.Build("test-provider", config, out _);
+
+        var act = async () => await pipeline.ExecuteAsync(async ct =>
+        {
+            await Task.Delay(TimeSpan.FromSeconds(10), ct);
+            return CreateSuccessResponse();
+        }, CancellationToken.None);
+
+        await act.Should().ThrowAsync<TimeoutRejectedException>();
+    }
+
+    [Fact]
+    public async Task Pipeline_ConfigValues_AppliedCorrectly()
+    {
+        var config = CreateTestConfig(maxAttempts: 4);
+        var pipeline = ProviderResiliencePipelineBuilder.Build("test-provider", config, out _);
+        var callCount = 0;
+
+        var result = await pipeline.ExecuteAsync(async ct =>
+        {
+            callCount++;
+            if (callCount <= 4)
+                throw new HttpRequestException("transient");
+            return CreateSuccessResponse();
+        }, CancellationToken.None);
+
+        callCount.Should().Be(5);
+        result.Should().NotBeNull();
+    }
+
+    private static ResilienceConfig CreateTestConfig(
+        int maxAttempts = 2,
+        double failureRatio = 0.5,
+        int minimumThroughput = 5,
+        int samplingDurationSeconds = 30,
+        int perAttemptSeconds = 30)
+    {
+        return new ResilienceConfig
+        {
+            Enabled = true,
+            Retry = new RetryConfig
+            {
+                MaxAttempts = maxAttempts,
+                BaseDelaySeconds = 0.01,
+                BackoffType = "Exponential"
+            },
+            CircuitBreaker = new CircuitBreakerConfig
+            {
+                FailureRatio = failureRatio,
+                SamplingDurationSeconds = samplingDurationSeconds,
+                MinimumThroughput = minimumThroughput,
+                BreakDurationSeconds = 60
+            },
+            Timeout = new TimeoutConfig
+            {
+                PerAttemptSeconds = perAttemptSeconds
+            }
+        };
+    }
+
+    private static ChatResponse CreateSuccessResponse()
+    {
+        return new ChatResponse([new ChatMessage(ChatRole.Assistant, "test response")]);
+    }
+}
diff --git a/src/Directory.Packages.props b/src/Directory.Packages.props
index 2bc0550..92de0e9 100644
--- a/src/Directory.Packages.props
+++ b/src/Directory.Packages.props
@@ -48,6 +48,7 @@
     <PackageVersion Include="Microsoft.Data.Sqlite" Version="10.0.5" />
 
     <!-- Infrastructure.AI -->
+    <PackageVersion Include="Polly.Core" Version="8.5.2" />
     <PackageVersion Include="Anthropic.SDK" Version="5.10.0" />
     <PackageVersion Include="Azure.AI.Inference" Version="1.0.0-beta.5" />
     <PackageVersion Include="Azure.AI.OpenAI" Version="2.8.0-beta.1" />
