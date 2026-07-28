using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Presentation.AgentHub.Tests;

/// <summary>
/// Shared authenticated-client construction for AgentHub wire-level controller tests. Replaces
/// the per-test-class <c>CreateAuthedClient</c> copies: every controller test that needs an
/// authenticated caller swaps the host's authentication for <see cref="TestAuthHandler"/> and
/// stamps the synthetic identity headers the same way, so the recipe lives once, here.
/// </summary>
public static class TestClientFactoryExtensions
{
    /// <summary>
    /// Creates an <see cref="HttpClient"/> whose requests authenticate through
    /// <see cref="TestAuthHandler"/> as the given synthetic user.
    /// </summary>
    /// <param name="factory">The web application factory to create the client from.</param>
    /// <param name="userId">The synthetic user id stamped into <see cref="TestAuthHandler.UserIdHeader"/>.</param>
    /// <param name="roles">Optional comma-separated role names stamped into <see cref="TestAuthHandler.RolesHeader"/>; omit for a role-less principal.</param>
    /// <param name="configureServices">Optional extra test-service configuration applied after the authentication swap (e.g. replacing a controller dependency with a mock).</param>
    /// <returns>The configured client.</returns>
    public static HttpClient CreateAuthedClient(
        this WebApplicationFactory<Program> factory,
        string userId = "alice@contoso.com",
        string? roles = null,
        Action<IServiceCollection>? configureServices = null)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var client = factory
            .WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            {
                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                        TestAuthHandler.SchemeName, _ => { });
                configureServices?.Invoke(services);
            }))
            .CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, userId);
        if (roles is not null)
            client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, roles);
        return client;
    }
}
