using Application.AI.Common.Interfaces;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using FluentAssertions;
using Infrastructure.AI.Factories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Infrastructure.AI.Tests.Factories;

public sealed class ChatClientFactoryTests : IDisposable
{
    private readonly ServiceCollection _services;
    private readonly AppConfig _appConfig;
    private readonly Mock<IOptionsMonitor<AppConfig>> _optionsMonitor;

    public ChatClientFactoryTests()
    {
        _services = new ServiceCollection();
        _appConfig = new AppConfig();
        _optionsMonitor = new Mock<IOptionsMonitor<AppConfig>>();
        _optionsMonitor.Setup(o => o.CurrentValue).Returns(_appConfig);
    }

    private ChatClientFactory CreateFactory(ServiceProvider? sp = null)
    {
        var provider = sp ?? _services.BuildServiceProvider();
        return new ChatClientFactory(_optionsMonitor.Object, provider);
    }

    [Fact]
    public void IsAvailable_AzureOpenAI_NoClientRegistered_ReturnsFalse()
    {
        using var factory = CreateFactory();

        factory.IsAvailable(AIAgentFrameworkClientType.AzureOpenAI).Should().BeFalse();
    }

    [Fact]
    public void IsAvailable_AzureAIInference_Configured_ReturnsTrue()
    {
        _appConfig.AI.AgentFramework.Endpoint = "https://test.openai.azure.com/";
        _appConfig.AI.AgentFramework.ApiKey = "test-key";
        _appConfig.AI.AgentFramework.ClientType = AIAgentFrameworkClientType.AzureAIInference;

        using var factory = CreateFactory();

        factory.IsAvailable(AIAgentFrameworkClientType.AzureAIInference).Should().BeTrue();
    }

    [Fact]
    public void IsAvailable_AzureAIInference_NotConfigured_ReturnsFalse()
    {
        // No endpoint or API key set
        using var factory = CreateFactory();

        factory.IsAvailable(AIAgentFrameworkClientType.AzureAIInference).Should().BeFalse();
    }

    [Fact]
    public void IsAvailable_PersistentAgents_NoAdminClient_ReturnsFalse()
    {
        using var factory = CreateFactory();

        factory.IsAvailable(AIAgentFrameworkClientType.PersistentAgents).Should().BeFalse();
    }

    [Fact]
    public void GetAvailableProviders_ReturnsAllClientTypes()
    {
        using var factory = CreateFactory();

        var providers = factory.GetAvailableProviders();

        providers.Should().ContainKey(AIAgentFrameworkClientType.AzureOpenAI);
        providers.Should().ContainKey(AIAgentFrameworkClientType.OpenAI);
        providers.Should().ContainKey(AIAgentFrameworkClientType.AzureAIInference);
        providers.Should().ContainKey(AIAgentFrameworkClientType.PersistentAgents);
        providers.Should().HaveCount(5);
    }

    [Fact]
    public async Task GetChatClientAsync_UnsupportedType_ThrowsArgumentException()
    {
        using var factory = CreateFactory();

        var act = () => factory.GetChatClientAsync(
            (AIAgentFrameworkClientType)999, "deployment");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var factory = CreateFactory();

        var act = () => factory.Dispose();

        act.Should().NotThrow();
    }

    // ── AzureAIInference uses correct SDK (not OpenAI) ────────────────────────

    [Fact]
    public async Task GetChatClientAsync_AzureAIInference_DoesNotWrapOpenAIChatClient()
    {
        // Regression: GetAzureAIInferenceChatClientAsync was using OpenAIClient which
        // sends "Authorization: Bearer <key>". Azure AI Foundry requires "api-key: <key>".
        // Fix: use Azure.AI.Inference.ChatCompletionsClient which sends the correct header.
        _appConfig.AI.AgentFramework.Endpoint = "https://test.services.ai.azure.com";
        _appConfig.AI.AgentFramework.ApiKey = "test-key";

        using var factory = CreateFactory();
        var client = await factory.GetChatClientAsync(
            AIAgentFrameworkClientType.AzureAIInference, "test-model");

        // Before fix: inner client is OpenAI.Chat.ChatClient (sends "Authorization: Bearer" — wrong for Azure)
        // After fix:  inner client is Azure.AI.Inference-based (sends "api-key" — correct for Azure)
        var openAiInner = client.GetService(typeof(OpenAI.Chat.ChatClient));
        openAiInner.Should().BeNull("AzureAIInference must not use OpenAI ChatClient — wrong auth header for Azure");
    }

    // ── NormalizeAzureAIInferenceEndpoint ──────────────────────────────────────

    [Theory]
    [InlineData("https://myresource.services.ai.azure.com", "https://myresource.services.ai.azure.com/models")]
    [InlineData("https://myresource.services.ai.azure.com/", "https://myresource.services.ai.azure.com/models")]
    public void NormalizeAzureAIInferenceEndpoint_ServicesAiAzureComWithNoPath_AppendsModels(
        string input, string expected)
    {
        var result = ChatClientFactory.NormalizeAzureAIInferenceEndpoint(new Uri(input));

        result.ToString().TrimEnd('/').Should().Be(expected);
    }

    [Theory]
    [InlineData("https://myresource.services.ai.azure.com/models")]
    [InlineData("https://myresource.services.ai.azure.com/openai")]
    public void NormalizeAzureAIInferenceEndpoint_ServicesAiAzureComWithPath_Unchanged(string input)
    {
        var uri = new Uri(input);

        var result = ChatClientFactory.NormalizeAzureAIInferenceEndpoint(uri);

        result.Should().Be(uri);
    }

    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("https://myresource.openai.azure.com/")]
    [InlineData("https://custom.endpoint.com/api/v2")]
    public void NormalizeAzureAIInferenceEndpoint_NonServicesAiAzureCom_Unchanged(string input)
    {
        var uri = new Uri(input);

        var result = ChatClientFactory.NormalizeAzureAIInferenceEndpoint(uri);

        result.Should().Be(uri);
    }

    public void Dispose()
    {
        // Ensure no leaked resources from test ServiceProviders
    }
}
