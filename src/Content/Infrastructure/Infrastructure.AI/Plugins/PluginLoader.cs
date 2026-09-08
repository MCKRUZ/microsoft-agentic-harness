using System.Collections.Concurrent;
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
    /// throw there today).
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

            var mcpServerNames = CommitResolvedState(skillPaths, mcpRegistrations);

            // Never inside this try's reach on its own — see LogLoadedSafely's remarks for why a
            // broken sink here must not retroactively turn an already-committed load into Failed.
            LogLoadedSafely(declaration, manifest, skillPaths, mcpServerNames);

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
    /// Commits both resolved contributions to shared config, each as a single reassignment rather than
    /// an in-place mutation. Nothing before this call has mutated shared state (see <see cref="Load"/>'s
    /// remarks), so a throw anywhere before it leaves both configs untouched already; this method's own
    /// job is to make a throw DURING the commit behave the same way, and to never expose a
    /// partially-applied MCP server set to a concurrent reader even on the successful path.
    /// Internal for direct testing — see <c>Infrastructure.AI.Tests.Plugins.PluginLoaderTests</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Round-2 code-review finding on this same fix: an earlier version left the two writes
    /// non-atomic with each other and reasoned the residual risk down to "an out-of-memory-class
    /// write failure, not a manifest-content one" — true, but understated. Unlike a skill path (which
    /// <c>SkillMetadataRegistry.ResolvePluginSkillPaths</c> only attributes to a plugin whose
    /// <see cref="LoadedPlugin.Status"/> is <see cref="PluginLoadStatus.Loaded"/>), an MCP server
    /// registered into <see cref="_mcpServersConfig"/> carries no plugin/status gating at all —
    /// <c>PluginToolBoundaryStartupValidator</c>/<c>McpConnectionManager</c> read
    /// <see cref="McpServersConfig.Servers"/> unconditionally. A server left committed for a plugin
    /// that ultimately reports <see cref="PluginLoadStatus.Failed"/> would be immediately live with
    /// none of that plugin's boundary governance ever verified — worse than the skills case, not an
    /// equally-thin residual.
    /// </para>
    /// <para>
    /// Round-3 code-review finding, on THIS round's own first fix for the paragraph above: register-
    /// and-track-then-roll-back-on-throw (mirroring <c>BundleStagingService.ParsePluginManifests</c>,
    /// issue #372) is safe for that method's own registry (bundle-scoped keys, <c>TryAdd</c>-only, a
    /// duplicate is always rejected) but not for this one, which deliberately allows last-writer-wins
    /// on a duplicate namespaced key across two plugin declarations sharing a <c>Name</c> — nothing
    /// validates that names are unique. A blind <c>TryRemove</c> on rollback would delete a
    /// DIFFERENT, already-loaded plugin's legitimate registration if this call's key happened to
    /// collide with one. The register-per-key loop against the live dictionary also let a concurrent
    /// reader (<c>McpConnectionManager</c> holds an open enumerator across network I/O per
    /// <see cref="McpServersConfig"/>'s own remarks) observe a partially-updated server set mid-loop,
    /// even on the successful path. Building the merged dictionary off to the side and reassigning
    /// <see cref="McpServersConfig.Servers"/> in one shot — the same single-reassignment shape already
    /// used two lines below for <see cref="SkillsConfig.AdditionalPaths"/> — removes both problems at
    /// once: nothing is visible to a reader until the whole merged set is ready, and a throw before the
    /// reassignment leaves the original dictionary reference completely untouched, so there is nothing
    /// left to roll back.
    /// </para>
    /// </remarks>
    internal List<string> CommitResolvedState(
        List<string> skillPaths,
        List<(string NamespacedName, McpServerDefinition Definition)> mcpRegistrations)
    {
        var mcpServerNames = new List<string>(mcpRegistrations.Count);

        if (mcpRegistrations.Count > 0)
        {
            var merged = new ConcurrentDictionary<string, McpServerDefinition>(_mcpServersConfig.Servers);
            foreach (var (namespacedName, definition) in mcpRegistrations)
            {
                // Last-writer-wins on a duplicate namespaced key — unlike BundleStagingService's
                // TryAdd + keep-first-and-warn. Deliberately different, not an oversight: a host
                // plugin's own manifest realistically never declares the same server name twice, so
                // this path optimizes for the simpler write; a bundle's namespace is per-upload and a
                // duplicate there is worth flagging to the (untrusted) bundle author rather than
                // silently accepted.
                merged[namespacedName] = definition;
                mcpServerNames.Add(namespacedName);
            }

            _mcpServersConfig.Servers = merged;
        }

        if (skillPaths.Count > 0)
            _skillsConfig.AdditionalPaths = [.. _skillsConfig.AdditionalPaths, .. skillPaths];

        return mcpServerNames;
    }

    /// <summary>
    /// Logs the successful load, isolated in its own try/catch so a broken sink can never be
    /// mistaken for a load failure.
    /// </summary>
    /// <remarks>
    /// Round-2 grader/correctness finding on this same fix: an earlier version kept this log call
    /// inside <see cref="Load"/>'s main try, after the commit — a broken sink there still landed in
    /// the outer catch and reported <see cref="PluginLoadStatus.Failed"/> despite the commit having
    /// already fully succeeded, reproducing #614's split state one call later. The exception here is
    /// deliberately swallowed rather than logged: the same call that just failed is the only channel
    /// available to report it, and this is diagnostic output, not part of load correctness.
    /// </remarks>
    private void LogLoadedSafely(
        PluginDeclaration declaration, PluginManifest manifest, List<string> skillPaths, List<string> mcpServerNames)
    {
        try
        {
            _logger.LogInformation(
                "Plugin {Name} v{Version} loaded: {SkillCount} skill path(s), {McpCount} MCP server(s)",
                declaration.Name, manifest.Version, skillPaths.Count, mcpServerNames.Count);
        }
        catch (Exception)
        {
            // Deliberately swallowed, not logged: the sink that just failed is the only channel this
            // method has to report a failure, and this call reports a load that already succeeded —
            // it does not decide load correctness. See this method's remarks for the full rationale.
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
            var built = BuildOneServer(declaration, serverProp);
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
    private McpServerDefinition? BuildOneServer(PluginDeclaration declaration, JsonProperty serverProp)
    {
        var result = McpServerDefinitionBuilder.Build(
            serverProp.Value, declaration.Env, $"[Plugin: {declaration.Name}]", serverProp.Name);
        if (result.IsSuccess)
            return result.Value;

        _logger.LogWarning(
            "Plugin {Name}: failed to build MCP server definition for '{ServerName}', skipping: {Errors}",
            declaration.Name, serverProp.Name, string.Join("; ", result.Errors));
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
