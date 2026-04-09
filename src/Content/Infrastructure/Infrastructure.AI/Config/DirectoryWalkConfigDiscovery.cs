using Application.AI.Common.Interfaces.Config;
using Domain.AI.Config;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Config;

/// <summary>
/// Discovers configuration files by walking the directory tree upward from a starting
/// directory to the filesystem root. Supports <c>@include</c> directives for file
/// composition and YAML frontmatter for path-scoped configuration.
/// </summary>
/// <remarks>
/// Discovery order mirrors Claude Code's config resolution:
/// <list type="bullet">
///   <item><c>AGENT.md</c> — Project scope</item>
///   <item><c>SKILL.md</c> — Project scope</item>
///   <item><c>.claude/rules/*.md</c> — Project scope</item>
///   <item><c>CLAUDE.md</c> — Project scope</item>
///   <item><c>CLAUDE.local.md</c> — Local scope (not checked in)</item>
/// </list>
/// Files closer to <paramref name="startDirectory"/> receive lower priority values
/// (higher effective priority). <c>@include</c> directives are resolved inline and
/// circular references are prevented via path tracking.
/// </remarks>
public sealed class DirectoryWalkConfigDiscovery : IConfigDiscoveryService
{
    private static readonly string[] ConfigFileNames =
    [
        "AGENT.md",
        "SKILL.md",
        "CLAUDE.md",
        "CLAUDE.local.md"
    ];

    private const string RulesSubdirectory = ".claude/rules";
    private const string FrontmatterDelimiter = "---";

    private readonly ILogger<DirectoryWalkConfigDiscovery> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="DirectoryWalkConfigDiscovery"/>.
    /// </summary>
    /// <param name="logger">Logger for diagnostic output during discovery.</param>
    public DirectoryWalkConfigDiscovery(ILogger<DirectoryWalkConfigDiscovery> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoveredConfigFile>> DiscoverAsync(
        string startDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);

        var fullStart = Path.GetFullPath(startDirectory);
        if (!Directory.Exists(fullStart))
        {
            _logger.LogWarning("Start directory does not exist: {Directory}", fullStart);
            return [];
        }

        var results = new List<DiscoveredConfigFile>();
        var processedIncludes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var depth = 0;
        var current = new DirectoryInfo(fullStart);

        while (current is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await ScanDirectoryAsync(current.FullName, depth, results, processedIncludes, cancellationToken);
            depth++;
            current = current.Parent;
        }

        results.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        return results.AsReadOnly();
    }

    /// <summary>
    /// Scans a single directory for known config files and rule files.
    /// </summary>
    private async Task ScanDirectoryAsync(
        string directoryPath,
        int depth,
        List<DiscoveredConfigFile> results,
        HashSet<string> processedIncludes,
        CancellationToken cancellationToken)
    {
        foreach (var fileName in ConfigFileNames)
        {
            var filePath = Path.Combine(directoryPath, fileName);
            if (!File.Exists(filePath))
                continue;

            var scope = fileName.EndsWith(".local.md", StringComparison.OrdinalIgnoreCase)
                ? ConfigScope.Local
                : ConfigScope.Project;

            var config = await ReadConfigFileAsync(filePath, scope, depth, processedIncludes, cancellationToken);
            if (config is not null)
            {
                results.Add(config);
                _logger.LogDebug("Discovered {Scope} config: {Path} (priority {Priority})", scope, filePath, depth);
            }
        }

        var rulesDir = Path.Combine(directoryPath, RulesSubdirectory);
        if (!Directory.Exists(rulesDir))
            return;

        var ruleFiles = Directory.GetFiles(rulesDir, "*.md");
        Array.Sort(ruleFiles, StringComparer.OrdinalIgnoreCase);

        foreach (var ruleFile in ruleFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var config = await ReadConfigFileAsync(ruleFile, ConfigScope.Project, depth, processedIncludes, cancellationToken);
            if (config is not null)
            {
                results.Add(config);
                _logger.LogDebug("Discovered rule file: {Path} (priority {Priority})", ruleFile, depth);
            }
        }
    }

    /// <summary>
    /// Reads a single config file, resolves <c>@include</c> directives, and parses frontmatter.
    /// </summary>
    private async Task<DiscoveredConfigFile?> ReadConfigFileAsync(
        string filePath,
        ConfigScope scope,
        int priority,
        HashSet<string> processedIncludes,
        CancellationToken cancellationToken)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        if (!processedIncludes.Add(normalizedPath))
        {
            _logger.LogWarning("Circular include detected, skipping: {Path}", normalizedPath);
            return null;
        }

        string rawContent;
        try
        {
            rawContent = await File.ReadAllTextAsync(normalizedPath, cancellationToken);
        }
        catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _logger.LogDebug("Config file does not exist, skipping: {Path}", normalizedPath);
            return null;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to read config file: {Path}", normalizedPath);
            return null;
        }

        var resolvedContent = await ResolveIncludesAsync(rawContent, normalizedPath, processedIncludes, cancellationToken);
        var pathGlobs = ParseFrontmatterPathGlobs(resolvedContent);

        return new DiscoveredConfigFile
        {
            FilePath = normalizedPath,
            Scope = scope,
            Priority = priority,
            Content = resolvedContent,
            PathGlobs = pathGlobs
        };
    }

    /// <summary>
    /// Resolves <c>@include</c> directives in file content. Supports three path forms:
    /// <list type="bullet">
    ///   <item><c>@./relative</c> — relative to the current file's directory</item>
    ///   <item><c>@~/path</c> — relative to user home directory</item>
    ///   <item><c>@/absolute</c> — absolute path (used as-is)</item>
    /// </list>
    /// Non-existent includes are silently skipped. Circular references are
    /// prevented by the shared <paramref name="processedIncludes"/> set.
    /// </summary>
    private async Task<string> ResolveIncludesAsync(
        string content,
        string sourceFilePath,
        HashSet<string> processedIncludes,
        CancellationToken cancellationToken)
    {
        var sourceDir = Path.GetDirectoryName(sourceFilePath) ?? string.Empty;
        var lines = content.Split('\n');
        var result = new List<string>(lines.Length);

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith('@') || trimmed.Length < 2)
            {
                result.Add(line);
                continue;
            }

            // Skip frontmatter delimiter false positives and lines that are clearly not includes
            var includePath = trimmed[1..].Trim();
            if (string.IsNullOrWhiteSpace(includePath))
            {
                result.Add(line);
                continue;
            }

            var resolvedPath = ResolveIncludePath(includePath, sourceDir);
            if (resolvedPath is null)
            {
                result.Add(line);
                continue;
            }

            var normalizedInclude = Path.GetFullPath(resolvedPath);
            if (!File.Exists(normalizedInclude))
            {
                _logger.LogDebug("Include target does not exist, skipping: {Path}", normalizedInclude);
                continue;
            }

            if (!processedIncludes.Add(normalizedInclude))
            {
                _logger.LogWarning("Circular include detected in resolution, skipping: {Path}", normalizedInclude);
                continue;
            }

            try
            {
                var includeContent = await File.ReadAllTextAsync(normalizedInclude, cancellationToken);
                var resolved = await ResolveIncludesAsync(includeContent, normalizedInclude, processedIncludes, cancellationToken);
                result.Add(resolved);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Failed to read include file: {Path}", normalizedInclude);
            }
        }

        return string.Join('\n', result);
    }

    /// <summary>
    /// Resolves an include path directive to an absolute filesystem path.
    /// Returns null if the path form is not recognized as an include directive.
    /// </summary>
    private static string? ResolveIncludePath(string includePath, string sourceDirectory)
    {
        if (includePath.StartsWith("./") || includePath.StartsWith(".\\"))
        {
            return Path.GetFullPath(Path.Combine(sourceDirectory, includePath));
        }

        if (includePath.StartsWith("~/") || includePath.StartsWith("~\\"))
        {
            var homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.GetFullPath(Path.Combine(homePath, includePath[2..]));
        }

        if (Path.IsPathRooted(includePath))
        {
            return Path.GetFullPath(includePath);
        }

        // Not a recognized include form — treat as regular content
        return null;
    }

    /// <summary>
    /// Parses YAML frontmatter between <c>---</c> delimiters to extract <c>paths:</c>
    /// glob patterns. Returns null if no frontmatter or no paths key is found.
    /// </summary>
    private static IReadOnlyList<string>? ParseFrontmatterPathGlobs(string content)
    {
        if (!content.TrimStart().StartsWith(FrontmatterDelimiter))
            return null;

        var firstDelimiter = content.IndexOf(FrontmatterDelimiter, StringComparison.Ordinal);
        if (firstDelimiter < 0)
            return null;

        var searchStart = firstDelimiter + FrontmatterDelimiter.Length;
        var secondDelimiter = content.IndexOf(FrontmatterDelimiter, searchStart, StringComparison.Ordinal);
        if (secondDelimiter < 0)
            return null;

        var frontmatter = content[searchStart..secondDelimiter];
        var frontmatterLines = frontmatter.Split('\n');

        foreach (var line in frontmatterLines)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("paths:", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = trimmed["paths:".Length..].Trim();
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var globs = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return globs.Length > 0 ? Array.AsReadOnly(globs) : null;
        }

        return null;
    }
}
