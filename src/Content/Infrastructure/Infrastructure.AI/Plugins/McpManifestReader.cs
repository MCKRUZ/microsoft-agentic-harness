using System.Text.Json;
using Domain.Common.Helpers;
using Microsoft.Extensions.Logging;

namespace Infrastructure.AI.Plugins;

/// <summary>
/// Locates, path-guards, and parses a manifest's <c>mcpServers</c> JSON block. Shared by
/// <see cref="PluginLoader"/> (host plugins) and <c>BundleStagingService</c> (bundle-declared servers)
/// so the two never independently decide how that file is found, guarded against escaping its owning
/// directory, or parsed — and so a malformed or missing file degrades identically (skip, log, never
/// fail the caller's larger operation) everywhere it's read.
/// </summary>
public static class McpManifestReader
{
    /// <summary>
    /// The parsed <c>mcpServers</c> object plus the <see cref="System.Text.Json.JsonDocument"/> that
    /// owns its underlying buffer. Callers must dispose this (via <c>using</c>) once done enumerating
    /// <see cref="ServersElement"/> — the element is only valid while the owning document is alive.
    /// </summary>
    public readonly struct McpServersBlock(JsonDocument document, JsonElement serversElement) : IDisposable
    {
        /// <summary>The <c>mcpServers</c> object's entries.</summary>
        public JsonElement ServersElement { get; } = serversElement;

        /// <inheritdoc />
        public void Dispose() => document.Dispose();
    }

    /// <summary>
    /// Resolves <paramref name="mcpServersRelativePath"/> against <paramref name="baseDir"/>, verifies it
    /// does not escape that directory, and parses its <c>mcpServers</c> object. Returns
    /// <see langword="null"/> when the path escapes, the file is missing, the JSON is malformed, it has no
    /// <c>mcpServers</c> property, or that property is not a JSON object (e.g. a string, array, or number)
    /// — every caller enumerates <see cref="McpServersBlock.ServersElement"/> as an object's properties,
    /// which throws <see cref="InvalidOperationException"/> on any other <see cref="JsonValueKind"/>; that
    /// exception must never propagate out of a manifest-parsing path this type exists to make degrade
    /// gracefully.
    /// </summary>
    /// <param name="baseDir">The directory the manifest declaring <paramref name="mcpServersRelativePath"/> lives in.</param>
    /// <param name="mcpServersRelativePath">The manifest's own <c>mcpServers</c> path (e.g. <c>"./mcp.json"</c>).</param>
    /// <param name="ownerDescription">Human-readable owner for log messages, e.g. <c>"Plugin azure"</c> or <c>"Bundle b1"</c>.</param>
    /// <param name="logger">Logger for the skip/failure cases.</param>
    public static McpServersBlock? ReadMcpServersBlock(
        string baseDir, string mcpServersRelativePath, string ownerDescription, ILogger logger)
    {
        string mcpPath;
        try
        {
            mcpPath = Path.GetFullPath(Path.Combine(baseDir, mcpServersRelativePath));
        }
        catch (Exception ex)
        {
            // Broad catch, matching this method's own JSON-parse guard below and the sibling
            // PluginManifestReader.Read's path-resolution guard: a narrower filter here would silently
            // diverge from both if a future runtime version throws a different exception type for a
            // malformed path than the ones known today.
            logger.LogWarning(ex,
                "{Owner}: MCP config path '{RelativePath}' is invalid, skipping",
                ownerDescription, mcpServersRelativePath);
            return null;
        }

        var normalizedBase = PathScope.Normalize(baseDir);
        if (!PathScope.IsSameOrUnderNormalized(PathScope.Normalize(mcpPath), normalizedBase))
        {
            logger.LogWarning(
                "{Owner}: MCP config path {Path} escapes its base directory, skipping", ownerDescription, mcpPath);
            return null;
        }

        if (!File.Exists(mcpPath))
        {
            logger.LogWarning("{Owner}: MCP config not found at {Path}, skipping", ownerDescription, mcpPath);
            return null;
        }

        JsonDocument doc;
        try
        {
            var json = File.ReadAllText(mcpPath);
            doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Owner}: failed to parse MCP config at {Path}", ownerDescription, mcpPath);
            return null;
        }

        if (doc.RootElement.TryGetProperty("mcpServers", out var serversElement))
        {
            if (serversElement.ValueKind == JsonValueKind.Object)
                return new McpServersBlock(doc, serversElement);

            logger.LogWarning(
                "{Owner}: 'mcpServers' in {Path} is a {Kind}, not a JSON object — skipping",
                ownerDescription, mcpPath, serversElement.ValueKind);
        }

        doc.Dispose();
        return null;
    }
}
