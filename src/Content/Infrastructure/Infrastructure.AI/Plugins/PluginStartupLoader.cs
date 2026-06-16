using Application.AI.Common.Interfaces.Plugins;
using Domain.Common.Config;
using Domain.Common.Config.AI.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.Plugins;

/// <summary>
/// One-shot startup driver for the plugin system. Runs via
/// <see cref="IHostedService.StartAsync"/> and resolves every declared plugin under
/// <c>AppConfig.AI.Plugins.Packages</c> into the live harness configuration before the
/// first agent turn.
/// </summary>
/// <remarks>
/// <para>
/// For each enabled <see cref="PluginDeclaration"/> the loader:
/// </para>
/// <list type="number">
///   <item><description>
///     Reads <c>plugin.json</c> via <see cref="IPluginManifestReader"/> (skipping the
///     declaration with a warning when the manifest is missing or invalid).
///   </description></item>
///   <item><description>
///     Wires the plugin's skill paths and MCP servers into the live config instances via
///     <see cref="IPluginLoader"/>. The loader mutates the same <c>SkillsConfig</c> /
///     <c>McpServersConfig</c> objects that <c>SkillMetadataRegistry</c> and
///     <c>McpConnectionManager</c> read — see <c>PluginLoader</c> DI registration.
///   </description></item>
///   <item><description>
///     Records the resulting <see cref="LoadedPlugin"/> in <see cref="IPluginRegistry"/>
///     (including <c>Disabled</c> entries for declarations turned off in config), so
///     boundary-governance providers such as <c>PluginPermissionRuleProvider</c> can emit
///     their bypass-immune <c>DeniedTools</c> rules.
///   </description></item>
/// </list>
/// <para>
/// <b>Ordering.</b> Skill discovery (<c>SkillMetadataRegistry</c>) and MCP connection
/// creation (<c>McpConnectionManager</c>) are both lazy — they read configuration on first
/// resolve, not at container build. Hosted services run after the container is built but
/// before the host begins serving requests, so populating the config and registry here
/// guarantees the merged plugin skills/servers are visible to the first discovery.
/// </para>
/// <para>
/// An empty <c>Packages</c> list is a clean no-op: the registry stays empty and no
/// configuration is mutated.
/// </para>
/// </remarks>
public sealed class PluginStartupLoader : IHostedService
{
    private readonly IOptionsMonitor<AppConfig> _config;
    private readonly IPluginManifestReader _manifestReader;
    private readonly IPluginLoader _loader;
    private readonly IPluginRegistry _registry;
    private readonly ILogger<PluginStartupLoader> _logger;

    /// <summary>Initializes a new instance of the <see cref="PluginStartupLoader"/> class.</summary>
    /// <param name="config">Monitor over the live application configuration.</param>
    /// <param name="manifestReader">Reader for <c>plugin.json</c> manifests.</param>
    /// <param name="loader">Loader that merges plugin capabilities into the live config.</param>
    /// <param name="registry">Registry that holds the loaded plugin records.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public PluginStartupLoader(
        IOptionsMonitor<AppConfig> config,
        IPluginManifestReader manifestReader,
        IPluginLoader loader,
        IPluginRegistry registry,
        ILogger<PluginStartupLoader> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(manifestReader);
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(logger);

        _config = config;
        _manifestReader = manifestReader;
        _loader = loader;
        _registry = registry;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var packages = _config.CurrentValue.AI.Plugins.Packages;
        if (packages.Count == 0)
        {
            _logger.LogDebug("No plugins declared under AppConfig.AI.Plugins.Packages — skipping plugin load");
            return Task.CompletedTask;
        }

        var loadedCount = 0;
        foreach (var declaration in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (LoadDeclaration(declaration))
                loadedCount++;
        }

        _logger.LogInformation(
            "Plugin load complete: {LoadedCount} of {DeclaredCount} declared plugin(s) loaded",
            loadedCount, packages.Count);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private bool LoadDeclaration(PluginDeclaration declaration)
    {
        if (string.IsNullOrWhiteSpace(declaration.Name))
        {
            _logger.LogWarning("Skipping plugin declaration with empty Name (Path: {Path})", declaration.Path);
            return false;
        }

        // Resolve the plugin directory against the executable base directory so relative
        // declaration paths behave the same as configured skill paths (which SkillMetadataRegistry
        // resolves against AppContext.BaseDirectory), independent of the launch CWD.
        var pluginPath = Path.IsPathRooted(declaration.Path)
            ? declaration.Path
            : Path.GetFullPath(declaration.Path, AppContext.BaseDirectory);

        if (!declaration.Enabled)
        {
            _logger.LogInformation("Plugin {Name} is disabled in configuration — recording as Disabled", declaration.Name);
            _registry.Register(DisabledPlugin(declaration, pluginPath));
            return false;
        }

        var manifest = _manifestReader.Read(pluginPath);
        if (manifest is null)
        {
            _logger.LogWarning(
                "Plugin {Name}: no valid plugin.json found at {Path} — skipping",
                declaration.Name, pluginPath);
            return false;
        }

        var loaded = _loader.Load(pluginPath, declaration, manifest);
        if (loaded is null)
        {
            _logger.LogWarning("Plugin {Name}: loader returned null — skipping", declaration.Name);
            return false;
        }

        _registry.Register(loaded);
        return loaded.Status == PluginLoadStatus.Loaded;
    }

    private static LoadedPlugin DisabledPlugin(PluginDeclaration declaration, string pluginPath) =>
        new(
            declaration.Name,
            Version: string.Empty,
            pluginPath,
            new PluginManifest { Name = declaration.Name },
            PluginLoadStatus.Disabled,
            SkillPaths: [],
            McpServerNames: [],
            declaration);
}
