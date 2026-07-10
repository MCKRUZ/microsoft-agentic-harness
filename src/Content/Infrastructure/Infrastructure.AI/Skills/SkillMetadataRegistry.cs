using Application.AI.Common.Interfaces;
using Application.AI.Common.Interfaces.Plugins;
using Domain.AI.Skills;
using Domain.Common.Config;
using Domain.Common.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Note: FileAgentSkillLoader and FileAgentSkill from Microsoft.Agents.AI are internal types.
// FileAgentSkillsProvider (public) handles progressive disclosure at runtime via AIContextProviders.
// This registry does its own filesystem walk for metadata-only discovery.

namespace Infrastructure.AI.Skills;

/// <summary>
/// Discovers and caches skill metadata by scanning filesystem directories for SKILL.md files.
/// </summary>
/// <remarks>
/// The framework's <c>FileAgentSkillLoader</c> is internal and not accessible directly.
/// This registry implements its own filesystem walk (up to <see cref="MaxSearchDepth"/> levels)
/// mirroring the same pattern. Skill content discovery at runtime is handled by
/// <c>FileAgentSkillsProvider</c> wired into <c>ChatClientAgentOptions.AIContextProviders</c>.
/// </remarks>
public sealed class SkillMetadataRegistry : ISkillMetadataRegistry
{
    private const int MaxSearchDepth = 3;

    private readonly ILogger<SkillMetadataRegistry> _logger;
    private readonly IOptionsMonitor<AppConfig> _appConfig;
    private readonly SkillMetadataParser _parser;
    private readonly IPluginRegistry? _pluginRegistry;

    private Dictionary<string, SkillDefinition>? _cache;
    private IReadOnlyList<string> _searchedPaths = [];
    private readonly Lock _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="SkillMetadataRegistry"/> class.
    /// </summary>
    /// <param name="logger">Logger for discovery diagnostics.</param>
    /// <param name="appConfig">Monitor over the live application configuration (skill search paths).</param>
    /// <param name="parser">Parser that reads a SKILL.md file into a <see cref="SkillDefinition"/>.</param>
    /// <param name="pluginRegistry">
    /// Optional registry of loaded plugins. When supplied, each discovered skill whose directory
    /// falls under a loaded plugin's <c>SkillPaths</c> is attributed to that plugin via
    /// <see cref="SkillDefinition.PluginSource"/>, which activates plugin boundary governance
    /// (AllowedTools/DeniedTools and Injected tool-resolution mode). Null in hosts that do not load
    /// plugins (for example the standalone MCP server), where all skills are treated as built-in.
    /// </param>
    public SkillMetadataRegistry(
        ILogger<SkillMetadataRegistry> logger,
        IOptionsMonitor<AppConfig> appConfig,
        SkillMetadataParser parser,
        IPluginRegistry? pluginRegistry = null)
    {
        _logger = logger;
        _appConfig = appConfig;
        _parser = parser;
        _pluginRegistry = pluginRegistry;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> SearchedPaths => _searchedPaths;

    /// <inheritdoc />
    public IReadOnlyList<SkillDefinition> GetAll()
    {
        EnsureLoaded();
        return [.. _cache!.Values];
    }

    /// <inheritdoc />
    public SkillDefinition? TryGet(string skillId)
    {
        EnsureLoaded();
        _cache!.TryGetValue(skillId, out var skill);
        return skill;
    }

    /// <inheritdoc />
    public IReadOnlyList<SkillDefinition> GetByCategory(string category)
    {
        EnsureLoaded();
        return _cache!.Values
            .Where(s => string.Equals(s.Category, category, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<SkillDefinition> GetByTags(IEnumerable<string> tags)
    {
        EnsureLoaded();
        var tagSet = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
        return _cache!.Values
            .Where(s => s.Tags.Any(t => tagSet.Contains(t)))
            .ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<SkillDefinition> GetBySkillType(string skillType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillType);
        EnsureLoaded();
        return _cache!.Values
            .Where(s => string.Equals(s.SkillType, skillType, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private void EnsureLoaded()
    {
        if (_cache is not null)
            return;

        lock (_lock)
        {
            if (_cache is not null)
                return;

            _cache = Discover();
        }
    }

    private Dictionary<string, SkillDefinition> Discover()
    {
        GuardPluginRegistryPresent();

        var skillsConfig = _appConfig.CurrentValue.AI?.Skills;
        var paths = skillsConfig?.AllPaths.ToList() ?? [];

        if (paths.Count == 0)
        {
            _logger.LogInformation("No skill paths configured in AppConfig.AI.Skills — skipping skill discovery");
            _searchedPaths = [];
            return new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);
        }

        // Resolve relative paths against AppContext.BaseDirectory (the bin folder)
        // so they match where csproj Content Include copies skills/agents at build time.
        // Avoids coupling configured paths to the process CWD, which differs between
        // `dotnet run` (project dir) and a published deployment (publish dir).
        var resolvedPaths = new List<string>();
        foreach (var p in paths)
        {
            var abs = Path.IsPathRooted(p) ? p : Path.GetFullPath(p, AppContext.BaseDirectory);
            if (Directory.Exists(abs))
                resolvedPaths.Add(abs);
            else
                _logger.LogWarning("Skill path not found, skipping: {Path}", abs);
        }

        _searchedPaths = resolvedPaths;

        if (resolvedPaths.Count == 0)
        {
            _logger.LogWarning("No valid skill paths found — skill discovery produced no results");
            return new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);
        var pluginPaths = ResolvePluginSkillPaths();

        foreach (var rootPath in resolvedPaths)
            DiscoverInDirectory(rootPath, depth: 0, pluginPaths, result);

        _logger.LogInformation(
            "Skill discovery complete: {Count} skills found across {PathCount} path(s)",
            result.Count, resolvedPaths.Count);

        return result;
    }

    /// <summary>
    /// Fails fast when the host declares plugins but no <see cref="IPluginRegistry"/> was registered.
    /// Without the registry, discovered skills can never be attributed to their owning plugin, so
    /// plugin boundary governance (AllowedTools/DeniedTools and AutonomyLevel) would silently no-op —
    /// a security-relevant misconfiguration. A clear startup exception is preferable to ungoverned
    /// plugin tools. Hosts that declare no plugins (for example the standalone MCP server) are
    /// unaffected: the registry stays legitimately optional there.
    /// </summary>
    private void GuardPluginRegistryPresent()
    {
        var declaredPlugins = _appConfig.CurrentValue.AI?.Plugins?.Packages?.Count ?? 0;
        if (declaredPlugins > 0 && _pluginRegistry is null)
        {
            throw new InvalidOperationException(
                $"{declaredPlugins} plugin(s) are declared under AppConfig.AI.Plugins.Packages, but no " +
                $"{nameof(IPluginRegistry)} is registered. Plugin boundary governance " +
                "(AllowedTools/DeniedTools/AutonomyLevel) cannot be enforced without it. Register the " +
                "plugin services (which include IPluginRegistry) or remove the plugin declarations.");
        }
    }

    /// <summary>
    /// Snapshots the skill directories of every successfully-loaded plugin, paired with the
    /// plugin name. Used to attribute each discovered skill to its owning plugin so boundary
    /// governance can apply. Empty when no plugin registry is available or no plugins are loaded.
    /// </summary>
    private IReadOnlyList<(string Path, string PluginName)> ResolvePluginSkillPaths()
    {
        if (_pluginRegistry is null)
            return [];

        var pairs = new List<(string Path, string PluginName)>();
        foreach (var plugin in _pluginRegistry.GetLoadedPlugins())
        {
            if (plugin.Status != PluginLoadStatus.Loaded)
                continue;

            foreach (var skillPath in plugin.SkillPaths)
            {
                if (string.IsNullOrWhiteSpace(skillPath))
                    continue;
                pairs.Add((PathScope.Normalize(skillPath), plugin.Name));
            }
        }

        return pairs;
    }

    private void DiscoverInDirectory(
        string directory,
        int depth,
        IReadOnlyList<(string Path, string PluginName)> pluginPaths,
        Dictionary<string, SkillDefinition> result)
    {
        if (depth > MaxSearchDepth)
            return;

        var skillFile = Path.Combine(directory, "SKILL.md");

        if (File.Exists(skillFile))
        {
            TryAddSkill(skillFile, directory, pluginPaths, result);

            // A directory with SKILL.md is a skill — don't recurse into it
            return;
        }

        // Recurse into subdirectories to find nested skills
        try
        {
            foreach (var subDir in Directory.EnumerateDirectories(directory))
                DiscoverInDirectory(subDir, depth + 1, pluginPaths, result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate directory: {Path}", directory);
        }
    }

    private void TryAddSkill(
        string skillFile,
        string directory,
        IReadOnlyList<(string Path, string PluginName)> pluginPaths,
        Dictionary<string, SkillDefinition> result)
    {
        try
        {
            var pluginSource = ResolveOwningPlugin(directory, pluginPaths);
            var definition = _parser.ParseFromFile(skillFile, directory, pluginSource);
            if (string.IsNullOrEmpty(definition.Id))
                return;

            // Config/built-in paths are walked before plugin paths (see SkillsConfig.AllPaths),
            // so the first definition for an ID wins. This prevents a plugin from shadowing a
            // built-in skill's system prompt via an ID collision.
            if (result.TryGetValue(definition.Id, out var existing))
            {
                _logger.LogWarning(
                    "Skill ID collision on '{SkillId}': keeping first from {ExistingPath} (source: {ExistingSource}); " +
                    "ignoring duplicate from {DuplicatePath} (source: {DuplicateSource})",
                    definition.Id,
                    existing.BaseDirectory, existing.PluginSource ?? "built-in",
                    directory, pluginSource ?? "built-in");
                return;
            }

            result[definition.Id] = definition;
            _logger.LogDebug(
                "Discovered skill: {SkillId} from {Path} (source: {Source})",
                definition.Id, directory, pluginSource ?? "built-in");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse skill from {Path}", skillFile);
        }
    }

    /// <summary>
    /// Returns the name of the loaded plugin that owns <paramref name="skillDirectory"/>, or null
    /// when the skill is built-in. A plugin owns the directory when the directory equals, or is
    /// nested under, one of the plugin's skill paths. The most specific (longest) matching path
    /// wins so nested plugin layouts attribute correctly.
    /// </summary>
    private static string? ResolveOwningPlugin(
        string skillDirectory,
        IReadOnlyList<(string Path, string PluginName)> pluginPaths)
    {
        if (pluginPaths.Count == 0)
            return null;

        var normalizedDir = PathScope.Normalize(skillDirectory);
        string? bestName = null;
        var bestLength = -1;

        foreach (var (path, pluginName) in pluginPaths)
        {
            if (PathScope.IsSameOrUnderNormalized(normalizedDir, path) && path.Length > bestLength)
            {
                bestName = pluginName;
                bestLength = path.Length;
            }
        }

        return bestName;
    }
}
