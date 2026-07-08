using Domain.Common.Config;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Presentation.Common.Extensions;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// Builds a service provider through the REAL composition root — the same
/// <c>RegisterConfigSections</c> + <c>BuildGlobalSolutionServices</c> pair that
/// <see cref="IServiceCollectionExtensions.GetServices"/> chains for every host
/// (AgentHub, ConsoleUI, EvalRunner, FoundryHost) — differing only in that
/// configuration comes from an in-memory dictionary instead of
/// <c>AppConfigHelper.LoadAppConfig()</c>'s appsettings.json files.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the Wave-2 audit's worst failure mode was "inert machinery":
/// features whose unit tests hand-construct the object graph with exactly the values
/// production never supplies. Tests built on this host bind the production service
/// graph, so a feature that is registered-but-never-wired (or wired with the wrong
/// lifetime) fails here even when its isolated unit tests pass.
/// </para>
/// <para>
/// The provider is built with <c>ValidateScopes = true</c> — stricter than the default
/// host — so captive-dependency bugs (a singleton capturing a scoped service, the
/// audit's H2 class) surface as resolution failures instead of silent stale state.
/// </para>
/// </remarks>
internal static class CompositionRootTestHost
{
    /// <summary>
    /// Composes the full production service graph from the given configuration values.
    /// </summary>
    /// <param name="settings">
    /// In-memory configuration (same key syntax as appsettings.json paths, e.g.
    /// <c>AppConfig:AI:Skills:BasePath</c>).
    /// </param>
    /// <param name="overrideServices">
    /// Optional post-composition overrides, receiving the built configuration. Use ONLY to
    /// replace external boundaries (LLM chat clients) or to supply a documented workaround
    /// for a known production wiring bug; everything else must stay production wiring or
    /// the test stops proving anything.
    /// </param>
    /// <returns>A scope-validating provider over the production composition.</returns>
    public static ServiceProvider BuildProvider(
        IEnumerable<KeyValuePair<string, string?>> settings,
        Action<IServiceCollection, IConfiguration>? overrideServices = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();

        // Real hosts get ILogger<T> from their HostBuilder before GetServices() runs;
        // a bare ServiceCollection needs the equivalent registration explicitly.
        services.AddLogging();

        // Mirror GetServices() exactly, minus the file-based config loading.
        services.RegisterConfigSections(configuration);
        var appConfig = configuration.GetSection("AppConfig").Get<AppConfig>() ?? new AppConfig();
        services.BuildGlobalSolutionServices(appConfig, includeHealthChecksUI: false);

        overrideServices?.Invoke(services, configuration);

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
        });
    }

    /// <summary>
    /// Runs the production <see cref="Infrastructure.AI.Plugins.PluginStartupLoader"/> hosted
    /// service exactly as a host does at boot: resolved from the registered
    /// <see cref="IHostedService"/> collection (proving the registration exists), started once
    /// before the first lazy skill/MCP discovery.
    /// </summary>
    /// <param name="provider">The composition-root provider to start plugin loading on.</param>
    public static async Task RunPluginStartupLoaderAsync(ServiceProvider provider)
    {
        var loader = provider.GetServices<IHostedService>()
            .OfType<Infrastructure.AI.Plugins.PluginStartupLoader>()
            .Single();

        await loader.StartAsync(CancellationToken.None);
    }
}
