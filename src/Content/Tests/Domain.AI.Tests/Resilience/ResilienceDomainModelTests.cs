using Domain.AI.Resilience;
using Xunit;

namespace Domain.AI.Tests.Resilience;

/// <summary>
/// Tests for resilience domain records, enums, and exception behavior.
/// </summary>
public sealed class ResilienceDomainModelTests
{
    [Fact]
    public void FallbackMetadata_NoFallback_IsFallbackFalse()
    {
        var metadata = new FallbackMetadata
        {
            ActiveProvider = "primary",
            IsFallback = false,
            FailedProviders = [],
            DisabledCapabilities = new HashSet<string>(),
            CircuitStates = new Dictionary<string, ProviderHealthState>
            {
                ["primary"] = ProviderHealthState.Healthy
            }
        };

        Assert.False(metadata.IsFallback);
        Assert.Empty(metadata.FailedProviders);
    }

    [Fact]
    public void FallbackMetadata_WithFallback_IsFallbackTrue()
    {
        var metadata = new FallbackMetadata
        {
            ActiveProvider = "secondary",
            IsFallback = true,
            FailedProviders = ["primary"],
            DisabledCapabilities = new HashSet<string>(),
            CircuitStates = new Dictionary<string, ProviderHealthState>
            {
                ["primary"] = ProviderHealthState.Unavailable,
                ["secondary"] = ProviderHealthState.Healthy
            }
        };

        Assert.True(metadata.IsFallback);
        Assert.Contains("primary", metadata.FailedProviders);
    }

    [Fact]
    public void FallbackMetadata_DisabledCapabilities_ReflectsProviderDiff()
    {
        var metadata = new FallbackMetadata
        {
            ActiveProvider = "fallback-provider",
            IsFallback = true,
            FailedProviders = ["primary"],
            DisabledCapabilities = new HashSet<string> { "vision", "streaming" },
            CircuitStates = new Dictionary<string, ProviderHealthState>()
        };

        Assert.Contains("vision", metadata.DisabledCapabilities);
        Assert.Contains("streaming", metadata.DisabledCapabilities);
        Assert.Equal(2, metadata.DisabledCapabilities.Count);
    }

    [Fact]
    public void ProviderExhaustedException_ContainsRetryAfterAndFailedProviders()
    {
        var failedProviders = new[] { "azure-openai", "anthropic" };
        var retryAfter = TimeSpan.FromSeconds(60);

        var exception = new ProviderExhaustedException(failedProviders, retryAfter);

        Assert.Equal(retryAfter, exception.RetryAfter);
        Assert.Equal(2, exception.FailedProviders.Count);
        Assert.Contains("azure-openai", exception.FailedProviders);
        Assert.Contains("anthropic", exception.FailedProviders);
        Assert.Contains("azure-openai", exception.Message);
        Assert.Contains("anthropic", exception.Message);
    }

    [Fact]
    public void ProviderExhaustedException_WithInnerException_WrapsCorrectly()
    {
        var inner = new InvalidOperationException("Rate limited");
        var exception = new ProviderExhaustedException(
            ["azure-openai"], TimeSpan.FromSeconds(30), inner);

        Assert.Same(inner, exception.InnerException);
        Assert.Contains("azure-openai", exception.Message);
    }

    [Fact]
    public void ProviderHealthState_NumericOrdering_EnablesComparison()
    {
        Assert.True(ProviderHealthState.Healthy < ProviderHealthState.Degraded);
        Assert.True(ProviderHealthState.Degraded < ProviderHealthState.Unavailable);
    }
}
