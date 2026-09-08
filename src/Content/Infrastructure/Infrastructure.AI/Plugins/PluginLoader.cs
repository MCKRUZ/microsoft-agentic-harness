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
    /// <remarks>
    /// <strong>Stages both steps before committing either.</strong> <see cref="ResolveSkillPaths"/>
    /// and <see cref="ResolveMcpServerRegistrations"/> compute what this plugin would contribute
    /// without touching <see cref="_skillsConfig"/>/<see cref="_mcpServersConfig"/> at all — only once
    /// BOTH have returned successfully does this method write either into shared state, right before
    /// constructing the <see cref="PluginLoadStatus.Loaded"/> result (#614). The earlier shape called
    /// <c>LoadSkills</c> (which committed to <see cref="_skillsConfig"/> immediately) and then
    /// <c>LoadMcpServers</c> in the same try block: if anything after the skill commit threw — a
    /// manifest problem, or something as mundane as a logging call failing — the catch below marked
    /// the plugin <see cref="PluginLoadStatus.Failed"/> while the skill path stayed registered.
    /// Because <c>SkillMetadataRegistry.ResolvePluginSkillPaths</c> only attributes a skill to a plugin
    /// whose <see cref="LoadedPlugin.Status"/> is <see cref="PluginLoadStatus.Loaded"/>, that skill's
    /// <c>PluginSource</c> resolved to <see langword="null"/> — indistinguishable from a built-in
    /// skill — so <c>ToolChainBuilder.ApplyPluginBoundaryIfPluginSkill</c> no-op'd and the skill ran
    /// with no <c>AllowedTools</c>/<c>DeniedTools</c> restriction at all, for exactly the plugin that
    /// failed to finish loading cleanly. Staging first removes the dependency on "nothing after the
    /// skill commit can ever throw" — an invariant nothing enforced and that held only because every
    /// call reachable from the MCP-loading step happens to already return <c>Result&lt;T&gt;</c>
    /// instead of throwing (traced during #614's investigation: no crafted manifest reproduces a live
    /// throw there today). This closes the gap by construction instead of by staying lucky.
    /// </remarks>
    public LoadedPlugin? Load(string pluginPath, PluginDeclaration declaration, PluginManifest manifest)
    {
        try
        {
            var skillPaths = string.IsNullOrEmpty(manifest.Skills)
                ? []
                : ResolveSkillPaths(pluginPath, declaration, manifest.Skills);

            var mcpRegistrations = string.IsNullOrEmpty(manifest.McpServers)
                ? []
                : ResolveMcpServerRegistrations(pluginPath, declaration, manifest.McpServers);

            // Commit point. Nothing above this line has mutated shared state, so every return above
            // it — early or via an exception — leaves both configs exactly as this call found them.
            if (skillPaths.Count > 0)
                _skillsConfig.AdditionalPaths = [.. _skillsConfig.AdditionalPaths, .. skillPaths];

            var mcpServerNames = new List<string>(mcpRegistrations.Count);
            foreach (var (namespacedName, definition) in mcpRegistrations)
            {
                // Last-writer-wins on a duplicate namespaced key — unlike BundleStagingService's
                // TryAdd + keep-first-and-warn. Deliberately different, not an oversight: a host
                // plugin's own manifest realistically never declares the same server name twice, so
                // this path optimizes for the simpler write; a bundle's namespace is per-upload and a
                // duplicate there is worth flagging to the (untrusted) bundle author rather than
                // silently accepted.
                _mcpServersConfig.Servers[namespacedName] = definition;
                mcpServerNames.Add(namespacedName);
            }

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

    /// <summary>
    /// Computes this plugin's skill directory without registering it — see <see cref="Load"/>'s
    /// remarks for why the two are kept apart.
    /// </summary>
    private List<string> ResolveSkillPaths(string pluginPath, PluginDeclaration declaration, string skillsRelativePath)
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

        _logger.LogInformation(
            "Plugin {Name}: resolved skill path {Path}",
            declaration.Name, skillsDir);

        return [skillsDir];
    }

    /// <summary>
    /// Computes this plugin's MCP server definitions without registering any of them — see
    /// <see cref="Load"/>'s remarks for why the two are kept apart.
    /// </summary>
    private List<(string NamespacedName, McpServerDefinition Definition)> ResolveMcpServerRegistrations(
        string pluginPath, PluginDeclaration declaration, string mcpRelativePath)
    {
        var registrations = new List<(string, McpServerDefinition)>();

        using var block = McpManifestReader.ReadMcpServersBlock(
            pluginPath, mcpRelativePath, $"Plugin {declaration.Name}", _logger);
        if (block is null)
            return registrations;

        foreach (var serverProp in block.Value.ServersElement.EnumerateObject())
        {
            var namespacedName = $"{declaration.Name}:{serverProp.Name}";
            var built = BuildOneServer(declaration, namespacedName, serverProp);
            if (built is not null)
                registrations.Add((namespacedName, built));
        }

        return registrations;
    }

    /// <summary>
    /// Builds one manifest-declared server definition, or <see langword="null"/> for a malformed entry
    /// (e.g. a non-string <c>args</c> element) — skipped and logged rather than failing the caller.
    /// <see cref="Load"/>'s outer catch would otherwise mark the WHOLE plugin
    /// <see cref="PluginLoadStatus.Failed"/> over one bad server, discarding the skill paths already
    /// resolved in the same call and leaving any server built by an earlier entry in this same loop
    /// unregistered (absent from the returned names, so nothing can deregister it later).
    /// </summary>
    private McpServerDefinition? BuildOneServer(PluginDeclaration declaration, string namespacedName, JsonProperty serverProp)
    {
        var result = McpServerDefinitionBuilder.Build(
            serverProp.Value, declaration.Env, $"[Plugin: {declaration.Name}]", serverProp.Name);
        if (result.IsSuccess)
            return result.Value;

        _logger.LogWarning(
            "Plugin {Name}: failed to build MCP server definition for '{ServerName}', skipping: {Errors}",
            declaration.Name, namespacedName, string.Join("; ", result.Errors));
        return null;
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
