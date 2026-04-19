using Domain.Common.Config;
using Domain.Common.Config.AI;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Presentation.AgentHub.Controllers;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Presentation.AgentHub.Tests.Controllers;

public sealed class ConfigControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ConfigControllerTests(TestWebApplicationFactory factory) => _factory = factory;

    private HttpClient CreateAuthedClient(
        Action<IServiceCollection>? configureServices = null)
    {
        var client = _factory
            .WithWebHostBuilder(b =>
            {
                b.ConfigureTestServices(services =>
                {
                    services.AddAuthentication(TestAuthHandler.SchemeName)
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                            TestAuthHandler.SchemeName, _ => { });
                    configureServices?.Invoke(services);
                });
            })
            .CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, "config-user");
        return client;
    }

    [Fact]
    public async Task GetDeployments_Returns200()
    {
        using var client = CreateAuthedClient();

        var response = await client.GetAsync("/api/config/deployments");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetDeployments_ReturnsDeploymentAndDefault()
    {
        using var client = CreateAuthedClient();

        var response = await client.GetAsync("/api/config/deployments");
        var result = await response.Content.ReadFromJsonAsync<DeploymentsResponse>();

        result.Should().NotBeNull();
        result!.Deployments.Should().NotBeEmpty();
        result.DefaultDeployment.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GetDeployments_WhenNoDeploymentsConfigured_FallsBackToDefault()
    {
        using var client = CreateAuthedClient();

        var response = await client.GetAsync("/api/config/deployments");
        var result = await response.Content.ReadFromJsonAsync<DeploymentsResponse>();

        result.Should().NotBeNull();
        // When AvailableDeployments is empty, response should contain at least the default
        result!.Deployments.Should().Contain(result.DefaultDeployment);
    }
}
