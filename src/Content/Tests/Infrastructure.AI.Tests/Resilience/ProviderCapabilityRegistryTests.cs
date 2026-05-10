using Domain.Common.Config.AI.Resilience;
using FluentAssertions;
using Infrastructure.AI.Resilience;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Resilience;

/// <summary>
/// Tests for <see cref="ProviderCapabilityRegistry"/> — config-driven capability
/// mapping and diff computation for provider fallback chains.
/// </summary>
public sealed class ProviderCapabilityRegistryTests
{
    [Fact]
    public void GetCapabilities_ConfiguredProvider_ReturnsFromConfig()
    {
        var registry = CreateRegistry(
            ("azure-openai", new ProviderCapabilitiesConfig
            {
                SupportsToolCalling = true,
                SupportsStreaming = true,
                SupportsVision = true,
                MaxTokens = 128000
            }));

        var caps = registry.GetCapabilities("azure-openai");

        caps.SupportsToolCalling.Should().BeTrue();
        caps.SupportsStreaming.Should().BeTrue();
        caps.SupportsVision.Should().BeTrue();
        caps.MaxTokens.Should().Be(128000);
    }

    [Fact]
    public void GetCapabilities_UnconfiguredProvider_ReturnsFullCapabilities()
    {
        var registry = CreateRegistry();

        var caps = registry.GetCapabilities("unknown-provider");

        caps.SupportsToolCalling.Should().BeTrue();
        caps.SupportsStreaming.Should().BeTrue();
        caps.SupportsVision.Should().BeTrue();
        caps.MaxTokens.Should().Be(int.MaxValue);
    }

    [Fact]
    public void DiffCapabilities_PrimaryHasVision_FallbackDoesNot_ReportsDisabled()
    {
        var registry = CreateRegistry(
            ("primary", new ProviderCapabilitiesConfig
            {
                SupportsToolCalling = true,
                SupportsStreaming = true,
                SupportsVision = true,
                MaxTokens = 128000
            }),
            ("fallback", new ProviderCapabilitiesConfig
            {
                SupportsToolCalling = true,
                SupportsStreaming = true,
                SupportsVision = false,
                MaxTokens = 200000
            }));

        var disabled = registry.DiffCapabilities("primary", "fallback");

        disabled.Should().ContainSingle().Which.Should().Be(ProviderCapabilityRegistry.Vision);
    }

    [Fact]
    public void DiffCapabilities_IdenticalProviders_NothingDisabled()
    {
        var registry = CreateRegistry(
            ("a", new ProviderCapabilitiesConfig
            {
                SupportsToolCalling = true,
                SupportsStreaming = true,
                SupportsVision = true,
                MaxTokens = 128000
            }),
            ("b", new ProviderCapabilitiesConfig
            {
                SupportsToolCalling = true,
                SupportsStreaming = true,
                SupportsVision = true,
                MaxTokens = 128000
            }));

        var disabled = registry.DiffCapabilities("a", "b");

        disabled.Should().BeEmpty();
    }

    private static ProviderCapabilityRegistry CreateRegistry(
        params (string deploymentId, ProviderCapabilitiesConfig capabilities)[] providers)
    {
        var config = new ResilienceConfig
        {
            Enabled = true,
            FallbackChain = providers.Select(p => new FallbackProviderConfig
            {
                DeploymentId = p.deploymentId,
                Capabilities = p.capabilities
            }).ToArray()
        };

        var monitor = new Mock<IOptionsMonitor<ResilienceConfig>>();
        monitor.Setup(m => m.CurrentValue).Returns(config);
        return new ProviderCapabilityRegistry(monitor.Object);
    }
}
