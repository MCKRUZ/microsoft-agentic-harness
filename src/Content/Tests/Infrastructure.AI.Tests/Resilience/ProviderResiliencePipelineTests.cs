using Domain.Common.Config.AI.Resilience;
using FluentAssertions;
using Infrastructure.AI.Resilience;
using Microsoft.Extensions.AI;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Xunit;

namespace Infrastructure.AI.Tests.Resilience;

/// <summary>
/// Tests for <see cref="ProviderResiliencePipelineBuilder"/> — verifies that per-provider
/// resilience pipelines correctly compose retry, circuit breaker, and timeout
/// strategies with config-driven parameters.
/// </summary>
public sealed class ProviderResiliencePipelineTests
{
    [Fact]
    public async Task Pipeline_TransientError_RetriesToConfiguredMax()
    {
        var config = CreateTestConfig(maxAttempts: 3);
        var pipeline = ProviderResiliencePipelineBuilder.Build("test-provider", config, out _);
        var callCount = 0;

        var result = await pipeline.ExecuteAsync(async ct =>
        {
            callCount++;
            if (callCount <= 2)
                throw new HttpRequestException("transient");
            return CreateSuccessResponse();
        }, CancellationToken.None);

        callCount.Should().Be(3);
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task Pipeline_Http429_TriggersRetry()
    {
        var config = CreateTestConfig(maxAttempts: 2);
        var pipeline = ProviderResiliencePipelineBuilder.Build("test-provider", config, out _);
        var callCount = 0;

        var result = await pipeline.ExecuteAsync(async ct =>
        {
            callCount++;
            if (callCount == 1)
                throw new HttpRequestException("Too Many Requests", null, System.Net.HttpStatusCode.TooManyRequests);
            return CreateSuccessResponse();
        }, CancellationToken.None);

        callCount.Should().Be(2);
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task Pipeline_Http500_TriggersRetry()
    {
        var config = CreateTestConfig(maxAttempts: 2);
        var pipeline = ProviderResiliencePipelineBuilder.Build("test-provider", config, out _);
        var callCount = 0;

        var result = await pipeline.ExecuteAsync(async ct =>
        {
            callCount++;
            if (callCount == 1)
                throw new HttpRequestException("Internal Server Error", null, System.Net.HttpStatusCode.InternalServerError);
            return CreateSuccessResponse();
        }, CancellationToken.None);

        callCount.Should().Be(2);
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task Pipeline_FailureRatioExceeded_OpensCircuit()
    {
        var config = CreateTestConfig(maxAttempts: 1, failureRatio: 0.5, minimumThroughput: 2, samplingDurationSeconds: 30);
        var pipeline = ProviderResiliencePipelineBuilder.Build("test-provider", config, out var stateProvider);

        for (var i = 0; i < 4; i++)
        {
            try
            {
                await pipeline.ExecuteAsync<ChatResponse>(async ct =>
                    throw new HttpRequestException("fail"), CancellationToken.None);
            }
            catch { }
        }

        var act = async () => await pipeline.ExecuteAsync(async ct => CreateSuccessResponse(), CancellationToken.None);
        await act.Should().ThrowAsync<BrokenCircuitException>();
    }

    [Fact]
    public async Task Pipeline_CircuitOpen_ThrowsBrokenCircuitExceptionWithoutInvokingDelegate()
    {
        var config = CreateTestConfig(maxAttempts: 1, failureRatio: 0.5, minimumThroughput: 2, samplingDurationSeconds: 30);
        var pipeline = ProviderResiliencePipelineBuilder.Build("test-provider", config, out _);

        for (var i = 0; i < 4; i++)
        {
            try
            {
                await pipeline.ExecuteAsync<ChatResponse>(async ct =>
                    throw new HttpRequestException("fail"), CancellationToken.None);
            }
            catch { }
        }

        var delegateCalled = false;
        var act = async () => await pipeline.ExecuteAsync(async ct =>
        {
            delegateCalled = true;
            return CreateSuccessResponse();
        }, CancellationToken.None);

        await act.Should().ThrowAsync<BrokenCircuitException>();
        delegateCalled.Should().BeFalse();
    }

    [Fact]
    public async Task Pipeline_SuccessAfterRetry_CircuitRemainsClosed()
    {
        var config = CreateTestConfig(maxAttempts: 2, failureRatio: 0.5, minimumThroughput: 4, samplingDurationSeconds: 30);
        var pipeline = ProviderResiliencePipelineBuilder.Build("test-provider", config, out var stateProvider);
        var callCount = 0;

        await pipeline.ExecuteAsync(async ct =>
        {
            callCount++;
            if (callCount == 1)
                throw new HttpRequestException("transient");
            return CreateSuccessResponse();
        }, CancellationToken.None);

        var result = await pipeline.ExecuteAsync(async ct => CreateSuccessResponse(), CancellationToken.None);

        result.Should().NotBeNull();
        stateProvider.CircuitState.Should().Be(CircuitState.Closed);
    }

    [Fact]
    public async Task Pipeline_Timeout_CancelsAttempt()
    {
        var config = CreateTestConfig(maxAttempts: 1, perAttemptSeconds: 1);
        var pipeline = ProviderResiliencePipelineBuilder.Build("test-provider", config, out _);

        var act = async () => await pipeline.ExecuteAsync(async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return CreateSuccessResponse();
        }, CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutRejectedException>();
    }

    [Fact]
    public async Task Pipeline_ConfigValues_AppliedCorrectly()
    {
        var config = CreateTestConfig(maxAttempts: 5);
        var pipeline = ProviderResiliencePipelineBuilder.Build("test-provider", config, out _);
        var callCount = 0;

        var result = await pipeline.ExecuteAsync(async ct =>
        {
            callCount++;
            if (callCount <= 4)
                throw new HttpRequestException("transient");
            return CreateSuccessResponse();
        }, CancellationToken.None);

        callCount.Should().Be(5);
        result.Should().NotBeNull();
    }

    private static ResilienceConfig CreateTestConfig(
        int maxAttempts = 2,
        double failureRatio = 0.5,
        int minimumThroughput = 5,
        int samplingDurationSeconds = 30,
        int perAttemptSeconds = 30)
    {
        return new ResilienceConfig
        {
            Enabled = true,
            Retry = new RetryConfig
            {
                MaxAttempts = maxAttempts,
                BaseDelaySeconds = 0.01,
                BackoffType = "Exponential"
            },
            CircuitBreaker = new CircuitBreakerConfig
            {
                FailureRatio = failureRatio,
                SamplingDurationSeconds = samplingDurationSeconds,
                MinimumThroughput = minimumThroughput,
                BreakDurationSeconds = 60
            },
            Timeout = new TimeoutConfig
            {
                PerAttemptSeconds = perAttemptSeconds
            }
        };
    }

    private static ChatResponse CreateSuccessResponse()
    {
        return new ChatResponse([new ChatMessage(ChatRole.Assistant, "test response")]);
    }
}
