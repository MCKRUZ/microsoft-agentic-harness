using Domain.Common.Config;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Presentation.Common.Extensions;

namespace Presentation.Common.Tests.Composition;

/// <summary>
/// Builds a service provider through the REAL composition root — the same
/// <c>RegisterConfigSections</c> + <c>BuildGlobalSolutionServices</c> pair that
/// <see cref="Presentation.Common.Extensions.IServiceCollectionExtensions.GetServices"/> chains for every host
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
        var (services, configuration) = ComposeServices(settings);

        overrideServices?.Invoke(services, configuration);

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
        });
    }

    /// <summary>
    /// Composes the full production service collection without building a provider, for tests that
    /// inspect the registrations themselves rather than resolving anything.
    /// </summary>
    /// <param name="settings">In-memory configuration, as for <see cref="BuildProvider"/>.</param>
    /// <returns>The composed collection and the configuration it was composed from.</returns>
    public static (ServiceCollection Services, IConfiguration Configuration) ComposeServices(
        IEnumerable<KeyValuePair<string, string?>> settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(WithOwnDocumentLoadDatabase(settings))
            .Build();

        var services = new ServiceCollection();

        // Real hosts get ILogger<T> from their HostBuilder before GetServices() runs;
        // a bare ServiceCollection needs the equivalent registration explicitly.
        services.AddLogging();

        // Mirror GetServices() exactly, minus the file-based config loading.
        services.RegisterConfigSections(configuration);
        var appConfig = configuration.GetSection("AppConfig").Get<AppConfig>() ?? new AppConfig();
        services.BuildGlobalSolutionServices(appConfig, includeHealthChecksUI: false);

        return (services, configuration);
    }

    // Every SQLite database a composition opens on its own, by the setting that names it, and whether that setting is a
    // file path or a connection string. Not the governance state database: its path must stay inside the application
    // directory, and it is opened only when durable governance state is turned on.
    private static readonly (string Key, string File, bool ConnectionString)[] OwnDatabases =
    [
        ("AppConfig:AI:Rag:Documents:LoadLedgerDatabasePath", "documents", false),
        ("AppConfig:AI:Erasure:JournalDatabasePath", "erasure-journal", false),
        ("AppConfig:AI:Conversations:DatabasePath", "conversations", false),
        ("AppConfig:AI:Planner:DatabasePath", "planner", false),
        ("AppConfig:AI:Schedules:DatabasePath", "schedules", false),
        ("AppConfig:AI:SkillTraining:CheckpointDatabasePath", "skill-training", false),
        ("AppConfig:AI:PromptUsage:ConnectionString", "prompt-usage", true),
        ("AppConfig:AI:EvalDashboard:ConnectionString", "eval-dashboard", true),
    ];

    /// <summary>
    /// Gives each composition its own copy of every SQLite database it opens, unless the test chose one. Compositions run
    /// in parallel, and on one shared default file two of them creating a schema at once fail with "table ... already
    /// exists" (#924: the document load ledger, then the conversation database, then the schedules database, each in
    /// turn). Placed under this run's state root, which is removed when the run ends.
    /// </summary>
    private static IEnumerable<KeyValuePair<string, string?>> WithOwnDocumentLoadDatabase(
        IEnumerable<KeyValuePair<string, string?>> settings)
    {
        var list = settings.ToList();
        ArgumentException.ThrowIfNullOrWhiteSpace(IsolatedStateRoot.Root, nameof(IsolatedStateRoot.Root));

        foreach (var (key, file, connectionString) in OwnDatabases)
        {
            if (list.Any(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase)))
                continue;
            var path = Path.Combine(IsolatedStateRoot.Root, $"{file}-{Guid.NewGuid():N}.db");
            list.Add(new(key, connectionString ? $"Data Source={path}" : path));
        }

        return list;
    }

    /// <summary>
    /// Runs the production <see cref="Infrastructure.AI.Plugins.PluginStartupLoader"/> hosted
    /// service exactly as a host does at boot: resolved from the registered
    /// <see cref="IHostedService"/> collection (proving the registration exists), started once
    /// before the first lazy skill/MCP discovery.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT also run <see cref="Infrastructure.AI.Plugins.PluginToolBoundaryStartupValidator"/>
    /// (#524's tool-existence check) — this suite's fixture plugin declares a synthetic
    /// <c>DeniedTools</c> entry ("dangerous_tool") that names no real first-party tool and no MCP
    /// server is configured here, so that validator would refuse to boot by design (#524's own
    /// behavior, correctly). This suite tests DOWNSTREAM enforcement of a plugin boundary
    /// (DeniedTools filtering, autonomy baselines, invocation governance) — #524's existence check
    /// itself has its own dedicated coverage in <c>PluginToolBoundaryStartupValidatorTests</c> and
    /// <c>PluginToolBoundaryTrackerTests</c>. See <see cref="PluginGovernanceCompositionTests"/>'s
    /// own call sites — each explicitly marks the boundary Verified after this step, simulating only
    /// the ONE fact (#524 already passed) this suite isn't itself exercising, not hand-constructing
    /// anything else in the graph.
    /// </remarks>
    /// <param name="provider">The composition-root provider to start plugin loading on.</param>
    public static async Task RunPluginStartupLoaderAsync(ServiceProvider provider)
    {
        var loader = provider.GetServices<IHostedService>()
            .OfType<Infrastructure.AI.Plugins.PluginStartupLoader>()
            .Single();

        await loader.StartAsync(CancellationToken.None);
    }
}
