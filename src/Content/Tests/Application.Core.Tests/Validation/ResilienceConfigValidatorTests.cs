using Application.Core.Validation;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.Resilience;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.Validation;

/// <summary>
/// Tests for <see cref="ResilienceConfigValidator"/>.
/// Pattern: CreateValidConfig() baseline, mutate one field per test.
/// When Enabled=false, FallbackChain can be empty. Numeric ranges always enforced.
/// </summary>
public class ResilienceConfigValidatorTests
{
    private readonly ResilienceConfigValidator _validator = new();

    [Fact]
    public async Task Validate_ValidConfig_NoErrors()
    {
        var config = CreateValidConfig();

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Validate_EmptyFallbackChain_HasError()
    {
        var config = CreateValidConfig();
        config.FallbackChain = [];

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "FallbackChain");
    }

    [Fact]
    public async Task Validate_NegativeFailureRatio_HasError()
    {
        var config = CreateValidConfig();
        config.CircuitBreaker.FailureRatio = -0.1;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "CircuitBreaker.FailureRatio");
    }

    [Fact]
    public async Task Validate_FailureRatioAboveOne_HasError()
    {
        var config = CreateValidConfig();
        config.CircuitBreaker.FailureRatio = 1.5;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "CircuitBreaker.FailureRatio");
    }

    [Fact]
    public async Task Validate_NegativeTimeout_HasError()
    {
        var config = CreateValidConfig();
        config.Timeout.PerAttemptSeconds = -1;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Timeout.PerAttemptSeconds");
    }

    [Fact]
    public async Task Validate_ZeroMaxQueueSize_HasError()
    {
        var config = CreateValidConfig();
        config.DegradedMode.MaxQueueSize = 0;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "DegradedMode.MaxQueueSize");
    }

    [Fact]
    public async Task Validate_MissingDeploymentId_HasError()
    {
        var config = CreateValidConfig();
        config.FallbackChain[0].DeploymentId = "";

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.Contains("DeploymentId"));
    }

    [Fact]
    public async Task Validate_DisabledConfig_SkipsChainValidation()
    {
        var config = CreateValidConfig();
        config.Enabled = false;
        config.FallbackChain = [];

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_NegativeRetryBaseDelay_HasError()
    {
        var config = CreateValidConfig();
        config.Retry.BaseDelaySeconds = -1;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Retry.BaseDelaySeconds");
    }

    [Fact]
    public async Task Validate_ZeroMinimumThroughput_HasError()
    {
        var config = CreateValidConfig();
        config.CircuitBreaker.MinimumThroughput = 0;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "CircuitBreaker.MinimumThroughput");
    }

    [Fact]
    public async Task Validate_InvalidBackoffType_HasError()
    {
        var config = CreateValidConfig();
        config.Retry.BackoffType = "Random";

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Retry.BackoffType");
    }

    [Fact]
    public async Task Validate_DisabledConfig_StillValidatesNumericRanges()
    {
        var config = CreateValidConfig();
        config.Enabled = false;
        config.FallbackChain = [];
        config.CircuitBreaker.FailureRatio = -1;

        var result = await _validator.ValidateAsync(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "CircuitBreaker.FailureRatio");
    }

    private static ResilienceConfig CreateValidConfig() => new()
    {
        Enabled = true,
        FallbackChain =
        [
            new FallbackProviderConfig
            {
                ClientType = AIAgentFrameworkClientType.AzureOpenAI,
                DeploymentId = "gpt-4o"
            },
            new FallbackProviderConfig
            {
                ClientType = AIAgentFrameworkClientType.Anthropic,
                DeploymentId = "claude-sonnet-4-20250514"
            }
        ],
        CircuitBreaker = new CircuitBreakerConfig
        {
            FailureRatio = 0.5,
            SamplingDurationSeconds = 30,
            MinimumThroughput = 5,
            BreakDurationSeconds = 60
        },
        Retry = new RetryConfig
        {
            MaxAttempts = 2,
            BaseDelaySeconds = 1.0,
            BackoffType = "Exponential"
        },
        Timeout = new TimeoutConfig { PerAttemptSeconds = 30 },
        DegradedMode = new DegradedModeConfig
        {
            RetryQueueTtlSeconds = 300,
            MaxQueueSize = 100
        }
    };
}
