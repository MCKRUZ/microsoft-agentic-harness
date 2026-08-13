using System.Text.Json;
using Domain.Common.Config.AI.MCP;

namespace Infrastructure.AI.Plugins;

/// <summary>
/// Builds an <see cref="McpServerDefinition"/> from one entry of a <c>mcpServers</c> JSON block.
/// Shared by <see cref="PluginLoader"/> (host-installed plugins) and bundle staging (an externally
/// authored bundle's own <c>plugin.json</c>/<c>mcp.json</c>), so the two never independently decide
/// how a server entry maps to a definition.
/// </summary>
public static class McpServerDefinitionBuilder
{
    /// <summary>
    /// Builds a server definition from one <c>mcpServers</c> JSON entry. Branches on the declared
    /// <c>type</c>: <c>"http"</c>/<c>"sse"</c> reads <c>url</c>; anything else (including an absent
    /// <c>type</c>, matching every existing manifest that predates this branch) is treated as
    /// <see cref="McpServerType.Stdio"/> and reads <c>command</c>/<c>args</c>/<c>env</c>.
    /// </summary>
    /// <param name="serverElement">The JSON value for this server's entry.</param>
    /// <param name="declarationEnv">
    /// Environment overrides from the owning plugin/bundle declaration, applied last so they take
    /// precedence over manifest-declared env — mirrors the precedence <see cref="PluginLoader"/> already
    /// enforced before this was extracted.
    /// </param>
    /// <param name="descriptionPrefix">A human-readable owner tag, e.g. <c>"[Plugin: azure]"</c>.</param>
    /// <param name="serverName">The server's own name, appended to <paramref name="descriptionPrefix"/>.</param>
    public static McpServerDefinition Build(
        JsonElement serverElement,
        IReadOnlyDictionary<string, string> declarationEnv,
        string descriptionPrefix,
        string serverName)
    {
        var type = ParseType(serverElement);

        var definition = new McpServerDefinition
        {
            Enabled = true,
            Type = type,
            Description = $"{descriptionPrefix} {serverName}"
        };

        if (type is McpServerType.Http or McpServerType.Sse)
        {
            var url = serverElement.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(url))
                throw new InvalidOperationException(
                    $"'{serverName}' declares a {type} transport with no 'url' — a remote server must declare one.");

            definition.Url = url;
        }
        else
        {
            if (serverElement.TryGetProperty("command", out var cmd))
                definition.Command = cmd.GetString() ?? string.Empty;

            if (serverElement.TryGetProperty("args", out var args))
                definition.Args = args.EnumerateArray()
                    .Select(a => a.GetString() ?? string.Empty)
                    .ToList();
        }

        if (serverElement.TryGetProperty("env", out var env))
        {
            foreach (var envProp in env.EnumerateObject())
                definition.Env[envProp.Name] = envProp.Value.GetString() ?? string.Empty;
        }

        // Declaration env overrides take precedence over manifest-declared env.
        foreach (var (key, value) in declarationEnv)
            definition.Env[key] = value;

        return definition;
    }

    /// <summary>
    /// Reads the optional <c>type</c> property, defaulting to <see cref="McpServerType.Stdio"/> — every
    /// manifest written before this branch existed omits the property and means stdio.
    /// </summary>
    private static McpServerType ParseType(JsonElement serverElement)
    {
        if (!serverElement.TryGetProperty("type", out var typeElement))
            return McpServerType.Stdio;

        var raw = typeElement.GetString();
        return raw?.ToLowerInvariant() switch
        {
            "http" => McpServerType.Http,
            "sse" => McpServerType.Sse,
            _ => McpServerType.Stdio
        };
    }
}
