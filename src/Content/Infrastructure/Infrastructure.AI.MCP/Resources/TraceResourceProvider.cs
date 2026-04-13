using Application.AI.Common.Interfaces;
using Domain.AI.MCP;
using Domain.Common.Config.MetaHarness;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.AI.MCP.Resources;

/// <summary>
/// Exposes optimization run trace files as MCP resources at <c>trace://{optimizationRunId}/{path}</c>.
/// </summary>
/// <remarks>
/// <para>
/// Requires JWT authentication on every operation. Throws <see cref="UnauthorizedAccessException"/>
/// when the caller's <see cref="McpRequestContext.IsAuthenticated"/> is <c>false</c>.
/// </para>
/// <para>
/// Gated by <see cref="MetaHarnessConfig.EnableMcpTraceResources"/>. When the flag is <c>false</c>,
/// <see cref="ListAsync"/> returns an empty list and <see cref="ReadAsync"/> throws
/// <see cref="InvalidOperationException"/>. Auth checks still run regardless of the flag.
/// </para>
/// <para>
/// Security: every path is fully resolved with <see cref="Path.GetFullPath"/> before containment
/// checks. The containment check uses a trailing-separator suffix to prevent
/// <c>/traces/run-1</c> falsely matching <c>/traces/run-10</c>.
/// On non-Windows, symlinks pointing outside the run directory are rejected.
/// </para>
/// <para>
/// URI scheme: <c>trace://{optimizationRunId}/{relativePath}</c>.
/// Directory layout: <c>{TraceDirectoryRoot}/optimizations/{optimizationRunId}/</c>.
/// </para>
/// </remarks>
public sealed class TraceResourceProvider : IMcpResourceProvider
{
    private const string Scheme = "trace://";

    private readonly IOptionsMonitor<MetaHarnessConfig> _config;
    private readonly ILogger<TraceResourceProvider> _logger;

    /// <summary>Initializes a new instance of <see cref="TraceResourceProvider"/>.</summary>
    public TraceResourceProvider(
        IOptionsMonitor<MetaHarnessConfig> config,
        ILogger<TraceResourceProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<McpResource>> ListAsync(
        string uri,
        McpRequestContext context,
        CancellationToken ct)
    {
        if (!context.IsAuthenticated)
            throw new UnauthorizedAccessException("MCP resource access requires authentication.");

        var cfg = _config.CurrentValue;
        if (!cfg.EnableMcpTraceResources)
            return [];

        if (!TryParseRunId(uri, out var runId))
            return [];

        var runRoot = ResolveRunRoot(cfg.TraceDirectoryRoot, runId);

        if (!Directory.Exists(runRoot))
            return [];

        var resources = new List<McpResource>();
        foreach (var file in Directory.EnumerateFiles(runRoot, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(runRoot, file).Replace('\\', '/');
            resources.Add(new McpResource(
                Uri: $"{Scheme}{runId}/{rel}",
                Name: Path.GetFileName(file)));
        }

        _logger.LogDebug(
            "Listed {Count} trace resources for optimization run '{RunId}'",
            resources.Count, runId);

        return resources;
    }

    /// <inheritdoc />
    public async Task<McpResourceContent> ReadAsync(
        string uri,
        McpRequestContext context,
        CancellationToken ct)
    {
        if (!context.IsAuthenticated)
            throw new UnauthorizedAccessException("MCP resource access requires authentication.");

        var cfg = _config.CurrentValue;
        if (!cfg.EnableMcpTraceResources)
            throw new FileNotFoundException($"Trace resource not found: '{uri}'. MCP trace resources are disabled.");

        if (!TryParseUri(uri, out var runId, out var relativePath))
            throw new ArgumentException($"Invalid or incomplete trace URI: '{uri}'.");

        // Traversal guard: reject before resolution
        if (relativePath.Contains(".."))
            throw new UnauthorizedAccessException($"Path traversal detected in URI: '{uri}'.");

        var runRoot = ResolveRunRoot(cfg.TraceDirectoryRoot, runId);
        var fullPath = Path.GetFullPath(Path.Combine(runRoot, relativePath));

        // Root containment: resolved path must stay within the run directory
        if (!IsPathSafe(fullPath, runRoot))
            throw new UnauthorizedAccessException(
                $"Path '{relativePath}' resolves outside the run directory.");

        // Symlink guard (non-Windows)
        if (!OperatingSystem.IsWindows())
        {
            var fileInfo = new FileInfo(fullPath);
            var realTarget = fileInfo.ResolveLinkTarget(returnFinalTarget: true);
            if (realTarget is not null)
            {
                var realPath = Path.GetFullPath(realTarget.FullName);
                if (!IsPathSafe(realPath, runRoot))
                    throw new UnauthorizedAccessException(
                        "File is a symlink pointing outside the run directory.");
            }
        }

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Trace resource not found: '{uri}'.", fullPath);

        var text = await File.ReadAllTextAsync(fullPath, ct);

        _logger.LogDebug("Read trace resource '{Uri}' ({Chars} chars)", uri, text.Length);

        return new McpResourceContent(uri, text);
    }

    private static string ResolveRunRoot(string traceRoot, string runId) =>
        Path.GetFullPath(Path.Combine(traceRoot, "optimizations", runId));

    /// <summary>Parses the optimization run ID from a <c>trace://{runId}/...</c> URI.</summary>
    private static bool TryParseRunId(string uri, out string runId)
    {
        runId = string.Empty;
        if (!uri.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) return false;

        var rest = uri[Scheme.Length..].TrimEnd('/');
        var slash = rest.IndexOf('/');
        runId = slash < 0 ? rest : rest[..slash];
        return !string.IsNullOrEmpty(runId);
    }

    /// <summary>
    /// Parses both run ID and relative path from a <c>trace://{runId}/{relativePath}</c> URI.
    /// Returns <c>false</c> when the URI has no path component after the run ID.
    /// </summary>
    private static bool TryParseUri(string uri, out string runId, out string relativePath)
    {
        runId = string.Empty;
        relativePath = string.Empty;

        if (!uri.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) return false;

        var rest = uri[Scheme.Length..];
        var slash = rest.IndexOf('/');
        if (slash < 0) return false; // no path component

        runId = rest[..slash];
        relativePath = rest[(slash + 1)..].Replace('/', Path.DirectorySeparatorChar);

        return !string.IsNullOrEmpty(runId) && !string.IsNullOrEmpty(relativePath);
    }

    private static bool IsPathSafe(string resolvedPath, string resolvedRoot)
    {
        var rootWithSep = resolvedRoot.TrimEnd(Path.DirectorySeparatorChar)
                          + Path.DirectorySeparatorChar;
        return resolvedPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)
               || string.Equals(resolvedPath, resolvedRoot, StringComparison.OrdinalIgnoreCase);
    }
}
