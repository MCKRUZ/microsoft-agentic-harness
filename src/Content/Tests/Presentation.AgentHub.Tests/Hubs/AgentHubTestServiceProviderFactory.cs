using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Presentation.AgentHub;

namespace Presentation.AgentHub.Tests.Hubs;

/// <summary>
/// Builds a service provider through the real <see cref="DependencyInjection.AddAgentHubServices"/>
/// composition, for DI-wiring tests that need to assert on what SignalR (or another registration)
/// actually resolves to. Shared by every test in this folder that was hand-rolling the same
/// dev-auth-bypass + in-memory-configuration + mocked-environment bootstrap.
/// </summary>
internal static class AgentHubTestServiceProviderFactory
{
    /// <summary>
    /// Builds the provider. <paramref name="configOverrides"/> is merged on top of the dev-auth
    /// bypass (<c>Auth:Disabled=true</c>, which avoids the Azure Identity/Microsoft.Identity.Web
    /// wiring so this stays a focused DI/wiring test) — pass additional <c>AppConfig:*</c> keys to
    /// exercise a specific config-driven registration path.
    /// </summary>
    public static ServiceProvider Build(IDictionary<string, string?>? configOverrides = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Auth:Disabled"] = "true",
        };
        if (configOverrides is not null)
        {
            foreach (var (key, value) in configOverrides)
                settings[key] = value;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns(Environments.Development);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentHubServices(configuration, environment.Object);

        return services.BuildServiceProvider();
    }
}
