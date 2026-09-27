using Azure;
using Azure.AI.OpenAI;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.AIFoundry;
using FluentAssertions;
using Infrastructure.AI.Factories;
using Infrastructure.AI.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Factories;

/// <summary>
/// Tests for <see cref="ChatClientFactory.IsAvailable"/> and
/// <see cref="ChatClientFactory.GetAvailableProviders"/> covering all provider
/// availability checks without real API credentials.
/// </summary>
public sealed class ChatClientFactoryAvailabilityTests : IDisposable
{
    private readonly ServiceCollection _services = new();

    public void Dispose()
    {
        // No-op: ServiceProvider is built per-test
    }

    private ChatClientFactory CreateFactory(
        AppConfig? config = null,
        ServiceCollection? services = null)
    {
        var appConfig = config ?? new AppConfig
        {
            AI = new AIConfig
            {
                AgentFramework = new AgentFrameworkConfig()
            }
        };

        var options = Mock.Of<IOptionsMonitor<AppConfig>>(o => o.CurrentValue == appConfig);
        var sc = services ?? _services;
        sc.AddSingleton(options);
        var sp = sc.BuildServiceProvider();

        return new ChatClientFactory(options, sp);
    }

    private const string FoundryDirectResponsesResourceEndpoint = "https://myresource.services.ai.azure.com";

    private static AppConfig CreateFoundryDirectResponsesConfig() => new()
    {
        AI = new AIConfig
        {
            AgentFramework = new AgentFrameworkConfig(),
            AIFoundry = new AIFoundryConfig { ResourceEndpoint = FoundryDirectResponsesResourceEndpoint }
        }
    };

    private static void RegisterFakeFoundryDirectResponsesClient(ServiceCollection services) =>
        services.AddKeyedSingleton(
            AgentFrameworkHelper.FoundryDirectResponsesClientKey,
            new AzureOpenAIClient(new Uri(FoundryDirectResponsesResourceEndpoint), new AzureKeyCredential("fake")));

    [Fact]
    public void IsAvailable_AzureOpenAI_NoClient_ReturnsFalse()
    {
        using var factory = CreateFactory();

        factory.IsAvailable(AIAgentFrameworkClientType.AzureOpenAI).Should().BeFalse();
    }

    [Fact]
    public void IsAvailable_OpenAI_NoClient_ReturnsFalse()
    {
        using var factory = CreateFactory();

        factory.IsAvailable(AIAgentFrameworkClientType.OpenAI).Should().BeFalse();
    }

    [Fact]
    public void IsAvailable_AzureAIInference_NoConfig_ReturnsFalse()
    {
        using var factory = CreateFactory();

        factory.IsAvailable(AIAgentFrameworkClientType.AzureAIInference).Should().BeFalse();
    }

    [Fact]
    public void IsAvailable_AzureAIInference_WithConfig_ReturnsTrue()
    {
        var config = new AppConfig
        {
            AI = new AIConfig
            {
                AgentFramework = new AgentFrameworkConfig
                {
                    Endpoint = "https://myresource.services.ai.azure.com",
                    ApiKey = "test-key",
                    ClientType = AIAgentFrameworkClientType.AzureAIInference
                }
            }
        };

        using var factory = CreateFactory(config);

        factory.IsAvailable(AIAgentFrameworkClientType.AzureAIInference).Should().BeTrue();
    }

    [Fact]
    public void IsAvailable_Anthropic_NoConfig_ReturnsFalse()
    {
        using var factory = CreateFactory();

        factory.IsAvailable(AIAgentFrameworkClientType.Anthropic).Should().BeFalse();
    }

    [Fact]
    public void IsAvailable_Anthropic_WithConfig_ReturnsTrue()
    {
        var config = new AppConfig
        {
            AI = new AIConfig
            {
                AgentFramework = new AgentFrameworkConfig
                {
                    Endpoint = "https://myresource.services.ai.azure.com",
                    ApiKey = "test-key",
                    ClientType = AIAgentFrameworkClientType.Anthropic
                }
            }
        };

        using var factory = CreateFactory(config);

        factory.IsAvailable(AIAgentFrameworkClientType.Anthropic).Should().BeTrue();
    }

    [Fact]
    public void IsAvailable_AnthropicDirect_NoConfig_ReturnsFalse()
    {
        using var factory = CreateFactory();

        factory.IsAvailable(AIAgentFrameworkClientType.AnthropicDirect).Should().BeFalse();
    }

    [Fact]
    public void IsAvailable_AnthropicDirect_ApiKeyOnly_ReturnsTrue()
    {
        // No Endpoint set at all — unlike Anthropic (via Foundry), AnthropicDirect must not
        // require one; AnthropicClient defaults to api.anthropic.com on its own.
        var config = new AppConfig
        {
            AI = new AIConfig
            {
                AgentFramework = new AgentFrameworkConfig
                {
                    ApiKey = "test-key",
                    ClientType = AIAgentFrameworkClientType.AnthropicDirect
                }
            }
        };

        using var factory = CreateFactory(config);

        factory.IsAvailable(AIAgentFrameworkClientType.AnthropicDirect).Should().BeTrue();
    }

    [Fact]
    public void IsAvailable_PersistentAgents_NoAdminClient_ReturnsFalse()
    {
        using var factory = CreateFactory();

        factory.IsAvailable(AIAgentFrameworkClientType.PersistentAgents).Should().BeFalse();
    }

    [Fact]
    public void IsAvailable_FoundryDirectResponses_NoConfig_ReturnsFalse()
    {
        using var factory = CreateFactory();

        factory.IsAvailable(AIAgentFrameworkClientType.FoundryDirectResponses).Should().BeFalse();
    }

    /// <summary>
    /// The config flag alone is not enough — <c>IsAvailable</c> must also confirm the keyed
    /// <see cref="AzureOpenAIClient"/> DI registration actually exists, the same "config says yes,
    /// but was it actually registered" split every other provider check makes.
    /// </summary>
    [Fact]
    public void IsAvailable_FoundryDirectResponses_ConfiguredButClientNotRegistered_ReturnsFalse()
    {
        using var factory = CreateFactory(CreateFoundryDirectResponsesConfig());

        factory.IsAvailable(AIAgentFrameworkClientType.FoundryDirectResponses).Should().BeFalse();
    }

    [Fact]
    public void IsAvailable_FoundryDirectResponses_ConfiguredWithClientRegistered_ReturnsTrue()
    {
        var services = new ServiceCollection();
        RegisterFakeFoundryDirectResponsesClient(services);

        using var factory = CreateFactory(CreateFoundryDirectResponsesConfig(), services);

        factory.IsAvailable(AIAgentFrameworkClientType.FoundryDirectResponses).Should().BeTrue();
    }

    [Fact]
    public void GetProviderStatus_FoundryDirectResponses_Unconfigured_ReportsMissingResourceEndpoint()
    {
        var config = new AppConfig
        {
            AI = new AIConfig
            {
                AgentFramework = new AgentFrameworkConfig { ClientType = AIAgentFrameworkClientType.FoundryDirectResponses }
            }
        };

        using var factory = CreateFactory(config);

        var status = factory.GetProviderStatus();

        status.IsConfigured.Should().BeFalse();
        status.MissingSettings.Should().ContainSingle().Which.Should().Be("AppConfig:AI:AIFoundry:ResourceEndpoint");
    }

    [Fact]
    public async Task GetChatClientAsync_FoundryDirectResponses_NotConfigured_ThrowsAiProviderNotConfiguredException()
    {
        using var factory = CreateFactory();

        var act = () => factory.GetChatClientAsync(AIAgentFrameworkClientType.FoundryDirectResponses, "my-deployment");

        await act.Should().ThrowAsync<Application.AI.Common.Exceptions.AiProviderNotConfiguredException>();
    }

    [Fact]
    public async Task GetChatClientAsync_FoundryDirectResponses_Configured_ReturnsChatClient()
    {
        var services = new ServiceCollection();
        RegisterFakeFoundryDirectResponsesClient(services);

        using var factory = CreateFactory(CreateFoundryDirectResponsesConfig(), services);

        var chatClient = await factory.GetChatClientAsync(AIAgentFrameworkClientType.FoundryDirectResponses, "my-deployment");

        chatClient.Should().NotBeNull();
    }

    [Fact]
    public void IsAvailable_UnknownType_ReturnsFalse()
    {
        using var factory = CreateFactory();

        factory.IsAvailable((AIAgentFrameworkClientType)999).Should().BeFalse();
    }

    [Fact]
    public void GetAvailableProviders_WithNoConfig_EchoAlwaysTrue()
    {
        using var factory = CreateFactory();

        var providers = factory.GetAvailableProviders();

        // Echo is always available (no external dependencies)
        providers[AIAgentFrameworkClientType.Echo].Should().BeTrue();

        // All other providers require configuration or DI registration
        providers[AIAgentFrameworkClientType.AzureOpenAI].Should().BeFalse();
        providers[AIAgentFrameworkClientType.OpenAI].Should().BeFalse();
        providers[AIAgentFrameworkClientType.AzureAIInference].Should().BeFalse();
        providers[AIAgentFrameworkClientType.PersistentAgents].Should().BeFalse();
        providers[AIAgentFrameworkClientType.Anthropic].Should().BeFalse();
        providers[AIAgentFrameworkClientType.FoundryResponses].Should().BeFalse();
        providers[AIAgentFrameworkClientType.FoundryDirectResponses].Should().BeFalse();
    }

    [Fact]
    public void GetProviderStatus_WhenUnconfigured_ReportsNotConfiguredWithMissingSettings()
    {
        var config = new AppConfig
        {
            AI = new AIConfig
            {
                AgentFramework = new AgentFrameworkConfig
                {
                    ClientType = AIAgentFrameworkClientType.Anthropic,
                    DefaultDeployment = "claude-sonnet-4-6"
                    // No Endpoint, no ApiKey
                }
            }
        };

        using var factory = CreateFactory(config);

        var status = factory.GetProviderStatus();

        status.IsConfigured.Should().BeFalse();
        status.ClientType.Should().Be(AIAgentFrameworkClientType.Anthropic);
        status.DefaultDeployment.Should().Be("claude-sonnet-4-6");
        status.MissingSettings.Should().Contain("AppConfig:AI:AgentFramework:ApiKey");
        status.MissingSettings.Should().Contain("AppConfig:AI:AgentFramework:Endpoint");
    }

    [Fact]
    public void GetProviderStatus_WhenConfigured_ReportsConfiguredWithNoMissingSettings()
    {
        var config = new AppConfig
        {
            AI = new AIConfig
            {
                AgentFramework = new AgentFrameworkConfig
                {
                    ClientType = AIAgentFrameworkClientType.Anthropic,
                    DefaultDeployment = "claude-sonnet-4-6",
                    Endpoint = "https://myresource.services.ai.azure.com",
                    ApiKey = "test-key"
                }
            }
        };

        using var factory = CreateFactory(config);

        var status = factory.GetProviderStatus();

        status.IsConfigured.Should().BeTrue();
        status.MissingSettings.Should().BeEmpty();
    }

    [Fact]
    public async Task GetChatClientAsync_UnsupportedType_Throws()
    {
        using var factory = CreateFactory();

        var act = () => factory.GetChatClientAsync(
            (AIAgentFrameworkClientType)999, "model");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Unsupported*");
    }

    [Fact]
    public async Task GetChatClientAsync_AzureOpenAI_NotRegistered_Throws()
    {
        using var factory = CreateFactory();

        var act = () => factory.GetChatClientAsync(
            AIAgentFrameworkClientType.AzureOpenAI, "gpt-4");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not configured*");
    }

    [Fact]
    public async Task GetChatClientAsync_OpenAI_NotRegistered_Throws()
    {
        using var factory = CreateFactory();

        var act = () => factory.GetChatClientAsync(
            AIAgentFrameworkClientType.OpenAI, "gpt-4");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not configured*");
    }

    [Fact]
    public async Task GetChatClientAsync_AzureAIInference_NoConfig_Throws()
    {
        using var factory = CreateFactory();

        var act = () => factory.GetChatClientAsync(
            AIAgentFrameworkClientType.AzureAIInference, "claude-sonnet");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not configured*");
    }

    [Fact]
    public async Task GetChatClientAsync_Anthropic_NoConfig_Throws()
    {
        using var factory = CreateFactory();

        var act = () => factory.GetChatClientAsync(
            AIAgentFrameworkClientType.Anthropic, "claude-sonnet");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not configured*");
    }

    [Fact]
    public async Task GetChatClientAsync_AnthropicDirect_NoConfig_Throws()
    {
        using var factory = CreateFactory();

        var act = () => factory.GetChatClientAsync(
            AIAgentFrameworkClientType.AnthropicDirect, "claude-opus");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not configured*");
    }

    [Fact]
    public async Task GetChatClientAsync_AnthropicDirect_WithApiKeyOnly_ReturnsClient()
    {
        // No Endpoint configured at all — proves construction succeeds against the SDK's own
        // default base address (api.anthropic.com), unlike Anthropic-via-Foundry which requires
        // Endpoint. Construction only builds SDK objects; it makes no network call.
        var config = new AppConfig
        {
            AI = new AIConfig
            {
                AgentFramework = new AgentFrameworkConfig
                {
                    ApiKey = "test-key",
                    ClientType = AIAgentFrameworkClientType.AnthropicDirect
                }
            }
        };
        using var factory = CreateFactory(config);

        var chatClient = await factory.GetChatClientAsync(
            AIAgentFrameworkClientType.AnthropicDirect, "claude-opus");

        chatClient.Should().NotBeNull();
    }

    [Fact]
    public async Task GetChatClientAsync_PersistentAgents_NoAdmin_Throws()
    {
        using var factory = CreateFactory();

        var act = () => factory.GetChatClientAsync(
            AIAgentFrameworkClientType.PersistentAgents, "agent-id");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not configured*");
    }

    [Fact]
    public async Task CreatePersistentAgentAsync_NoAdmin_Throws()
    {
        using var factory = CreateFactory();

        var act = () => factory.CreatePersistentAgentAsync("gpt-4", "test-agent");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not configured*");
    }

    [Fact]
    public async Task GetChatClientAsync_AzureAIInference_InvalidUri_Throws()
    {
        var config = new AppConfig
        {
            AI = new AIConfig
            {
                AgentFramework = new AgentFrameworkConfig
                {
                    Endpoint = "not-a-valid-uri",
                    ApiKey = "test-key",
                    ClientType = AIAgentFrameworkClientType.AzureAIInference
                }
            }
        };

        using var factory = CreateFactory(config);

        var act = () => factory.GetChatClientAsync(
            AIAgentFrameworkClientType.AzureAIInference, "model");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Invalid*");
    }
}
