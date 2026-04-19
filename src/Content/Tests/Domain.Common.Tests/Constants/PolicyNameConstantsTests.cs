using Domain.Common.Constants;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Constants;

/// <summary>
/// Tests for <see cref="PolicyNameConstants"/> ensuring policy name values are stable.
/// </summary>
public class PolicyNameConstantsTests
{
    [Fact]
    public void HttpCircuitBreaker_HasExpectedValue()
    {
        PolicyNameConstants.HTTP_CIRCUIT_BREAKER.Should().Be("HttpCircuitBreaker");
    }

    [Fact]
    public void HttpRetry_HasExpectedValue()
    {
        PolicyNameConstants.HTTP_RETRY.Should().Be("HttpRetry");
    }

    [Fact]
    public void HttpTimeout_HasExpectedValue()
    {
        PolicyNameConstants.HTTP_TIMEOUT.Should().Be("HttpTimeout");
    }

    [Fact]
    public void CorsConfigPolicy_HasExpectedValue()
    {
        PolicyNameConstants.CORS_CONFIG_POLICY.Should().Be("CorsConfigPolicy");
    }

    [Fact]
    public void CorsMcpServerPolicy_HasExpectedValue()
    {
        PolicyNameConstants.CORS_AI_MCPSERVER_POLICY.Should().Be("CorsAIMCPServerPolicy");
    }

    [Fact]
    public void CorsCopilotPolicy_HasExpectedValue()
    {
        PolicyNameConstants.CORS_AI_COPILOT_POLICY.Should().Be("CorsAICopilotPolicy");
    }

    [Fact]
    public void RateLimiterDefaultPolicy_HasExpectedValue()
    {
        PolicyNameConstants.RATE_LIMITER_AI_DEFAULT_POLICY.Should().Be("RateLimiterAIDefaultPolicy");
    }

    [Fact]
    public void RateLimiterMcpServerPolicy_HasExpectedValue()
    {
        PolicyNameConstants.RATE_LIMITER_AI_MCPSERVER_POLICY.Should().Be("RateLimiterAIMCPServerPolicy");
    }

    [Fact]
    public void AllConstants_AreDistinct()
    {
        var values = new[]
        {
            PolicyNameConstants.HTTP_CIRCUIT_BREAKER,
            PolicyNameConstants.HTTP_RETRY,
            PolicyNameConstants.HTTP_TIMEOUT,
            PolicyNameConstants.CORS_CONFIG_POLICY,
            PolicyNameConstants.CORS_AI_MCPSERVER_POLICY,
            PolicyNameConstants.CORS_AI_COPILOT_POLICY,
            PolicyNameConstants.RATE_LIMITER_AI_DEFAULT_POLICY,
            PolicyNameConstants.RATE_LIMITER_AI_MCPSERVER_POLICY
        };

        values.Should().OnlyHaveUniqueItems();
    }
}
