using FluentAssertions;
using Infrastructure.AI.Factories;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Infrastructure.AI.Tests.Connectivity;

/// <summary>
/// Integration tests that verify live connectivity to Azure AI Foundry.
/// Requires user secrets configured for the agentic-harness-console-ui project.
/// Run with: dotnet test --filter "Category=Integration"
/// Skip in CI with: dotnet test --filter "Category!=Integration"
/// </summary>
[Trait("Category", "Integration")]
public class AzureAIConnectivityTests
{
    private readonly string? _endpoint;
    private readonly string? _apiKey;
    private readonly string? _deployment;

    public AzureAIConnectivityTests()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets("agentic-harness-console-ui")
            .Build();

        _endpoint = config["AppConfig:AI:AgentFramework:Endpoint"];
        _apiKey = config["AppConfig:AI:AgentFramework:ApiKey"];
        _deployment = config["AppConfig:AI:AgentFramework:DefaultDeployment"];
    }

    /// <summary>
    /// Raw HTTP call to verify the endpoint and key work end-to-end.
    /// Azure AI Foundry Anthropic endpoint uses x-api-key (Anthropic convention, not Azure api-key).
    /// </summary>
    [Fact]
    public async Task Anthropic_RawHttp_SendSimpleMessage_ReturnsResponse()
    {
        EnsureCredentials();

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("x-api-key", _apiKey);
        http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        var url = _endpoint!.TrimEnd('/') + "/anthropic/v1/messages";
        var body = $$"""
            {
              "model": "{{_deployment}}",
              "max_tokens": 20,
              "messages": [{ "role": "user", "content": "Say hello in one word." }]
            }
            """;

        var response = await http.PostAsync(url,
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        var responseBody = await response.Content.ReadAsStringAsync();

        response.IsSuccessStatusCode.Should().BeTrue(
            $"Expected 2xx but got {(int)response.StatusCode}. Body: {responseBody}");
        responseBody.Should().Contain("content");
    }

    [Fact]
    public void AzureAIInference_EndpointNormalization_ProducesCorrectUrl()
    {
        EnsureCredentials();

        var raw = new Uri(_endpoint!);
        var normalized = ChatClientFactory.NormalizeAzureAIInferenceEndpoint(raw);

        // services.ai.azure.com endpoints must have /models path appended
        if (raw.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase))
            normalized.AbsolutePath.Should().Be("/models");
        else
            normalized.Should().Be(raw);
    }

    private void EnsureCredentials()
    {
        if (string.IsNullOrWhiteSpace(_endpoint) ||
            string.IsNullOrWhiteSpace(_apiKey) ||
            string.IsNullOrWhiteSpace(_deployment))
        {
            throw new InvalidOperationException(
                "Azure AI credentials not found in user secrets. " +
                "Set them with: dotnet user-secrets set \"AppConfig:AI:AgentFramework:Endpoint\" \"...\" " +
                "--project src/Content/Presentation/Presentation.ConsoleUI");
        }
    }
}
