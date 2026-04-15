using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using Xunit;

namespace Presentation.AgentHub.Tests;

/// <summary>
/// Integration tests verifying that Presentation.AgentHub wires authentication,
/// CORS, SignalR, and rate limiting correctly after section-02.
/// </summary>
[Trait("Category", "CoreSetup")]
public class CoreSetupTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public CoreSetupTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GET_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/api/agents");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GET_WithValidTestToken_Returns200()
    {
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                        TestAuthHandler.SchemeName, _ => { });
            });
        }).CreateClient();

        var response = await client.GetAsync("/api/agents");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Options_CorsPreflightFromLocalhost5173_ReturnsAllowedHeaders()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/agents");
        request.Headers.Add("Origin", "http://localhost:5173");
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "Authorization");

        var response = await client.SendAsync(request);

        Assert.True(
            (int)response.StatusCode is >= 200 and < 300,
            $"Expected 2xx CORS preflight response but got {(int)response.StatusCode}");
        Assert.True(
            response.Headers.Contains("Access-Control-Allow-Origin"),
            "CORS response must include Access-Control-Allow-Origin header");
    }

    [Fact]
    public async Task UseCors_BeforeUseAuthentication_PreflightDoesNotReturn401()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/agents");
        request.Headers.Add("Origin", "http://localhost:5173");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        var response = await client.SendAsync(request);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SignalRUpgrade_WithoutToken_IsRejected()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        // SignalR negotiate endpoint — requires [Authorize] on AgentTelemetryHub.
        var response = await client.PostAsync("/hubs/agent/negotiate?negotiateVersion=1", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task McpInvoke_Called11TimesRapidly_Returns429OnEleventh()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        // Dispose each response to avoid socket exhaustion; capture the status code.
        var lastStatus = HttpStatusCode.OK;
        for (int i = 0; i < 11; i++)
        {
            using var response = await client.PostAsync("/api/mcp/tools/any/invoke", null);
            lastStatus = response.StatusCode;
        }

        // The GlobalLimiter in DependencyInjection.cs limits POST /api/mcp/tools/*
        // to 10 req/min per IP. The 11th request must be rejected.
        Assert.Equal(HttpStatusCode.TooManyRequests, lastStatus);
    }
}
