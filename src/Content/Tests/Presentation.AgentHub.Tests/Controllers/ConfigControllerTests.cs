using Application.AI.Common.Interfaces;
using Application.AI.Common.Models;
using Domain.Common.Config;
using Domain.Common.Config.AI;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Presentation.AgentHub.Controllers;
using Presentation.AgentHub.Tests.Fakes;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Presentation.AgentHub.Tests.Controllers;

public sealed class ConfigControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ConfigControllerTests(TestWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task GetDeployments_Returns200()
    {
        using var client = _factory.CreateAuthedClient("config-user");

        var response = await client.GetAsync("/api/config/deployments");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetDeployments_ReturnsDeploymentAndDefault()
    {
        using var client = _factory.CreateAuthedClient("config-user");

        var response = await client.GetAsync("/api/config/deployments");
        var result = await response.Content.ReadFromJsonAsync<DeploymentsResponse>();

        result.Should().NotBeNull();
        result!.Deployments.Should().NotBeEmpty();
        result.DefaultDeployment.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GetDeployments_WhenNoDeploymentsConfigured_FallsBackToDefault()
    {
        using var client = _factory.CreateAuthedClient("config-user");

        var response = await client.GetAsync("/api/config/deployments");
        var result = await response.Content.ReadFromJsonAsync<DeploymentsResponse>();

        result.Should().NotBeNull();
        // When AvailableDeployments is empty, response should contain at least the default
        result!.Deployments.Should().Contain(result.DefaultDeployment);
    }

    [Fact]
    public async Task GetStatus_WhenProviderUnconfigured_ReportsMissingSettings()
    {
        var missing = new[] { "AppConfig:AI:AgentFramework:Endpoint", "AppConfig:AI:AgentFramework:ApiKey" };
        using var client = _factory.CreateAuthedClient("config-user", configureServices: services =>
        {
            services.AddSingleton<IChatClientFactory>(new FakeChatClientFactory
            {
                ProviderStatus = new AiProviderStatus(
                    AIAgentFrameworkClientType.Anthropic, "claude-sonnet-4-6",
                    IsConfigured: false, MissingSettings: missing),
            });
        });

        var response = await client.GetAsync("/api/config/status");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<AiProviderStatusResponse>();
        result.Should().NotBeNull();
        result!.Configured.Should().BeFalse();
        result.ClientType.Should().Be("Anthropic");
        result.MissingSettings.Should().BeEquivalentTo(missing);
    }

    [Fact]
    public async Task GetStatus_WhenProviderConfigured_ReportsConfigured()
    {
        using var client = _factory.CreateAuthedClient("config-user", configureServices: services =>
        {
            services.AddSingleton<IChatClientFactory>(new FakeChatClientFactory
            {
                ProviderStatus = new AiProviderStatus(
                    AIAgentFrameworkClientType.Anthropic, "claude-sonnet-4-6",
                    IsConfigured: true, MissingSettings: []),
            });
        });

        var response = await client.GetAsync("/api/config/status");
        var result = await response.Content.ReadFromJsonAsync<AiProviderStatusResponse>();

        result.Should().NotBeNull();
        result!.Configured.Should().BeTrue();
        result.MissingSettings.Should().BeEmpty();
    }
}
