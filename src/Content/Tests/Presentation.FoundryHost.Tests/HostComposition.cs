using Domain.Common.Config;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Presentation.Common.Extensions;

namespace Presentation.FoundryHost.Tests;

/// <summary>
/// Builds the host's production service graph (<c>RegisterConfigSections</c> + <c>BuildGlobalSolutionServices</c>),
/// bound with this host's own bootstrap overrides and its state rooted in a given <c>$HOME</c>.
/// </summary>
internal static class HostComposition
{
    /// <summary>Composes the services.</summary>
    /// <param name="home">The <c>$HOME</c> the host roots its state under.</param>
    /// <param name="settings">Configuration a test sets on top of the host's overrides, by <c>:</c>-separated key.</param>
    /// <param name="replace">Registrations a test adds after the production ones, so they win.</param>
    public static ServiceProvider Build(
        string home,
        IReadOnlyDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? replace = null)
    {
        var values = FoundryHostBootstrap.BuildConfigOverrides(name => name == "HOME" ? home : null)
            .ToDictionary(o => o.Key.Replace("__", ":"), o => (string?)o.Value);
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
            values[key] = value;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterConfigSections(configuration);
        var appConfig = configuration.GetSection("AppConfig").Get<AppConfig>() ?? new AppConfig();
        services.BuildGlobalSolutionServices(appConfig, includeHealthChecksUI: false);
        replace?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
