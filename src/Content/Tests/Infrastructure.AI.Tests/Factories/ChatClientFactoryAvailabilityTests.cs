using Domain.Common.Config;
using Domain.Common.Config.AI;
using FluentAssertions;
using Infrastructure.AI.Factories;
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
    public void IsAvailable_PersistentAgents_NoAdminClient_ReturnsFalse()
    {
        using var factory = CreateFactory();

        factory.IsAvailable(AIAgentFrameworkClientType.PersistentAgents).Should().BeFalse();
    }

    [Fact]
    public void IsAvailable_UnknownType_ReturnsFalse()
    {
        using var factory = CreateFactory();

        factory.IsAvailable((AIAgentFrameworkClientType)999).Should().BeFalse();
    }

    [Fact]
    public void GetAvailableProviders_ReturnsAllFiveTypes()
    {
        using var factory = CreateFactory();

        var providers = factory.GetAvailableProviders();

        providers.Should().HaveCount(5);
        providers.Should().ContainKey(AIAgentFrameworkClientType.AzureOpenAI);
        providers.Should().ContainKey(AIAgentFrameworkClientType.OpenAI);
        providers.Should().ContainKey(AIAgentFrameworkClientType.AzureAIInference);
        providers.Should().ContainKey(AIAgentFrameworkClientType.PersistentAgents);
        providers.Should().ContainKey(AIAgentFrameworkClientType.Anthropic);
    }

    [Fact]
    public void GetAvailableProviders_WithNoConfig_AllFalse()
    {
        using var factory = CreateFactory();

        var providers = factory.GetAvailableProviders();

        providers.Values.Should().AllSatisfy(v => v.Should().BeFalse());
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
