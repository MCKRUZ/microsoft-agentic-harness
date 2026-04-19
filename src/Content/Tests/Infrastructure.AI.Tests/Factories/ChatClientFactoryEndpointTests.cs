using FluentAssertions;
using Infrastructure.AI.Factories;
using Xunit;

namespace Infrastructure.AI.Tests.Factories;

/// <summary>
/// Tests for <see cref="ChatClientFactory.NormalizeAzureAIInferenceEndpoint"/> covering
/// endpoint normalization logic for Azure AI Foundry multi-model resources.
/// </summary>
public sealed class ChatClientFactoryEndpointTests
{
    [Fact]
    public void NormalizeEndpoint_AzureServicesNoPath_AppendsModels()
    {
        var endpoint = new Uri("https://myresource.services.ai.azure.com");

        var result = ChatClientFactory.NormalizeAzureAIInferenceEndpoint(endpoint);

        result.AbsolutePath.Should().Be("/models");
    }

    [Fact]
    public void NormalizeEndpoint_AzureServicesTrailingSlash_AppendsModels()
    {
        var endpoint = new Uri("https://myresource.services.ai.azure.com/");

        var result = ChatClientFactory.NormalizeAzureAIInferenceEndpoint(endpoint);

        result.AbsolutePath.Should().Be("/models");
    }

    [Fact]
    public void NormalizeEndpoint_AzureServicesWithPath_PreservesOriginal()
    {
        var endpoint = new Uri("https://myresource.services.ai.azure.com/openai/deployments");

        var result = ChatClientFactory.NormalizeAzureAIInferenceEndpoint(endpoint);

        result.Should().Be(endpoint);
    }

    [Fact]
    public void NormalizeEndpoint_NonAzureEndpoint_PreservesOriginal()
    {
        var endpoint = new Uri("https://api.openai.com");

        var result = ChatClientFactory.NormalizeAzureAIInferenceEndpoint(endpoint);

        result.Should().Be(endpoint);
    }

    [Fact]
    public void NormalizeEndpoint_NonAzureWithServicesSubstring_PreservesOriginal()
    {
        var endpoint = new Uri("https://my-custom-ai-service.example.com");

        var result = ChatClientFactory.NormalizeAzureAIInferenceEndpoint(endpoint);

        result.Should().Be(endpoint);
    }

    [Fact]
    public void NormalizeEndpoint_AzureServicesWithModelsPath_PreservesOriginal()
    {
        var endpoint = new Uri("https://myresource.services.ai.azure.com/models");

        var result = ChatClientFactory.NormalizeAzureAIInferenceEndpoint(endpoint);

        result.Should().Be(endpoint);
    }

    [Fact]
    public void NormalizeEndpoint_CaseInsensitive_AppendsModels()
    {
        var endpoint = new Uri("https://myresource.SERVICES.AI.AZURE.COM");

        var result = ChatClientFactory.NormalizeAzureAIInferenceEndpoint(endpoint);

        result.AbsolutePath.Should().Be("/models");
    }
}
