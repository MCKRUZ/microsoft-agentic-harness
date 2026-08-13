using System.Text.Json;
using Application.AI.Common.Interfaces.Plugins;
using Domain.Common.Config.AI;
using Domain.Common.Config.AI.MCP;
using Domain.Common.Config.AI.Plugins;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Plugins;

/// <summary>
/// Wires a plugin's skills and MCP servers into the harness configuration.
/// Skills are added to <see cref="SkillsConfig.AdditionalPaths"/>; MCP servers are merged
/// into <see cref="McpServersConfig.Servers"/> under namespaced keys (plugin-name:server-name).
/// </summary>
public sealed class PluginLoader : IPluginLoader
{
    private readonly SkillsConfig _skillsConfig;
    private readonly McpServersConfig _mcpServersConfig;
    private readonly ILogger<PluginLoader> _logger;

    /// <summary>Initializes a new instance of <see cref="PluginLoader"/>.</summary>
    public PluginLoader(
        SkillsConfig skillsConfig,
        McpServersConfig mcpServersConfig,
        ILogger<PluginLoader> logger)
    {
        _skillsConfig = skillsConfig;
        _mcpServersConfig = mcpServersConfig;
        _logger = logger;
    }

    /// <inheritdoc />
    public LoadedPlugin? Load(string pluginPath, PluginDeclaration declaration, PluginManifest manifest)
    {
        var skillPaths = new List<string>();
        var mcpServerNames = new List<string>();

        try
        {
            if (!string.IsNullOrEmpty(manifest.Skills))
                skillPaths.AddRange(LoadSkills(pluginPath, declaration, manifest.Skills));

            if (!string.IsNullOrEmpty(manifest.McpServers))
                mcpServerNames.AddRange(LoadMcpServers(pluginPath, declaration, manifest.McpServers));

            _logger.LogInformation(
                "Plugin {Name} v{Version} loaded: {SkillCount} skill path(s), {McpCount} MCP server(s)",
                declaration.Name, manifest.Version, skillPaths.Count, mcpServerNames.Count);

            return new LoadedPlugin(
                declaration.Name,
                manifest.Version,
                pluginPath,
                manifest,
                PluginLoadStatus.Loaded,
                skillPaths,
                mcpServerNames,
                declaration);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load plugin {Name}", declaration.Name);

            return new LoadedPlugin(
                declaration.Name,
                manifest.Version,
                pluginPath,
                manifest,
                PluginLoadStatus.Failed,
                [],
                [],
                declaration);
        }
    }

    private List<string> LoadSkills(string pluginPath, PluginDeclaration declaration, string skillsRelativePath)
    {
        var skillsDir = Path.GetFullPath(Path.Combine(pluginPath, skillsRelativePath))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!IsContainedWithin(skillsDir, pluginPath))
        {
            _logger.LogWarning(
                "Plugin {Name}: skills path {Path} escapes plugin directory, skipping",
                declaration.Name, skillsDir);
            return [];
        }

        if (!Directory.Exists(skillsDir))
        {
            _logger.LogDebug(
                "Plugin {Name}: skills directory not found at {Path}",
                declaration.Name, skillsDir);
            return [];
        }

        _skillsConfig.AdditionalPaths = _skillsConfig.AdditionalPaths.Append(skillsDir).ToList();

        _logger.LogInformation(
            "Plugin {Name}: added skill path {Path}",
            declaration.Name, skillsDir);

        return [skillsDir];
    }

    private List<string> LoadMcpServers(string pluginPath, PluginDeclaration declaration, string mcpRelativePath)
    {
        var names = new List<string>();

        using var block = McpManifestReader.ReadMcpServersBlock(
            pluginPath, mcpRelativePath, $"Plugin {declaration.Name}", _logger);
        if (block is null)
            return names;

        foreach (var serverProp in block.Value.ServersElement.EnumerateObject())
        {
            var namespacedName = $"{declaration.Name}:{serverProp.Name}";
            if (TryBuildAndRegisterOneServer(declaration, namespacedName, serverProp))
                names.Add(namespacedName);
        }

        return names;
    }

    /// <summary>
    /// Builds one manifest-declared server and registers it under <paramref name="namespacedName"/>.
    /// A malformed entry (e.g. a non-string <c>args</c> element) is skipped and logged rather than
    /// thrown — <see cref="Load"/>'s outer catch would otherwise mark the WHOLE plugin
    /// <see cref="PluginLoadStatus.Failed"/> over one bad server, discarding the skill paths already
    /// collected in the same call and leaving any server registered by an earlier entry in this same
    /// loop orphaned (absent from the returned names, so nothing can deregister it later).
    /// </summary>
    private bool TryBuildAndRegisterOneServer(PluginDeclaration declaration, string namespacedName, JsonProperty serverProp)
    {
        McpServerDefinition definition;
        try
        {
            definition = McpServerDefinitionBuilder.Build(
                serverProp.Value, declaration.Env, $"[Plugin: {declaration.Name}]", serverProp.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Plugin {Name}: failed to build MCP server definition for '{ServerName}', skipping",
                declaration.Name, serverProp.Name);
            return false;
        }

        // Last-writer-wins on a duplicate namespaced key — unlike BundleStagingService's TryAdd +
        // keep-first-and-warn. Deliberately different, not an oversight: a host plugin's own manifest
        // realistically never declares the same server name twice, so this path optimizes for the
        // simpler write; a bundle's namespace is per-upload and a duplicate there is worth flagging to
        // the (untrusted) bundle author rather than silently accepted.
        _mcpServersConfig.Servers[namespacedName] = definition;
        return true;
    }

    private static bool IsContainedWithin(string resolvedPath, string basePath)
    {
        var canonicalBase = Path.GetFullPath(basePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var canonicalTarget = Path.GetFullPath(resolvedPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return canonicalTarget.StartsWith(canonicalBase + Path.DirectorySeparatorChar, comparison)
            || string.Equals(canonicalTarget, canonicalBase, comparison);
    }
}
