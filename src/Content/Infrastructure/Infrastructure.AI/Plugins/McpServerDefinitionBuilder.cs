using System.Text.Json;
using Domain.Common;
using Domain.Common.Config.AI.MCP;
using Domain.Common.Extensions;

namespace Infrastructure.AI.Plugins;

/// <summary>
/// Builds an <see cref="McpServerDefinition"/> from one entry of a <c>mcpServers</c> JSON block.
/// Shared by <see cref="PluginLoader"/> (host-installed plugins) and bundle staging (an externally
/// authored bundle's own <c>plugin.json</c>/<c>mcp.json</c>), so the two never independently decide
/// how a server entry maps to a definition.
/// </summary>
/// <remarks>
/// Every field this class reads comes from externally-authored, untrusted manifest JSON — a bundle's
/// own <c>mcp.json</c>, or (with more trust, but still hand-authored) a host-installed plugin's. A
/// malformed shape (a number where a string is expected, an object where an array is expected) is an
/// ordinary, expected input-validation failure, not an exceptional condition, so <see cref="Build"/>
/// returns <see cref="Result{T}"/> rather than throwing (issue #374) — matching this repo's own
/// convention that validation failures on untrusted input use <c>Result&lt;T&gt;</c>, not exceptions.
/// </remarks>
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
    /// <returns>
    /// The built definition, or a failure describing which property had the wrong JSON shape (or, for
    /// an http/sse entry, a missing <c>url</c>) — caller-safe: it names the property and server, never
    /// the surrounding manifest content.
    /// </returns>
    public static Result<McpServerDefinition> Build(
        JsonElement serverElement,
        IReadOnlyDictionary<string, string> declarationEnv,
        string descriptionPrefix,
        string serverName)
    {
        // Every property read below goes through JsonElement.TryGetProperty on serverElement itself
        // (directly, via ReadOptionalString/ReadArgs/ReadEnv) — TryGetProperty throws
        // InvalidOperationException outright when called on a non-Object element, so a manifest entry
        // like "badserver": "not-an-object" would otherwise throw here uncaught instead of failing
        // cleanly, exactly the defect this Result-returning signature exists to close.
        if (serverElement.ValueKind is not JsonValueKind.Object)
            return Result<McpServerDefinition>.Fail(
                $"'{serverName}' declares its server entry as {serverElement.ValueKind}, but an object was expected.");

        var typeResult = ParseType(serverElement, serverName);
        if (!typeResult.IsSuccess)
            return Result<McpServerDefinition>.Fail([.. typeResult.Errors]);

        var definition = new McpServerDefinition
        {
            Enabled = true,
            Type = typeResult.Value,
            Description = $"{descriptionPrefix} {serverName}"
        };

        var transportResult = ReadTransportFields(serverElement, typeResult.Value, serverName, definition);
        if (!transportResult.IsSuccess)
            return Result<McpServerDefinition>.Fail([.. transportResult.Errors]);

        var envResult = ReadEnv(serverElement, serverName);
        if (!envResult.IsSuccess)
            return Result<McpServerDefinition>.Fail([.. envResult.Errors]);

        if (envResult.Value is not null)
            foreach (var (key, value) in envResult.Value)
                definition.Env[key] = value;

        // Declaration env overrides take precedence over manifest-declared env.
        foreach (var (key, value) in declarationEnv)
            definition.Env[key] = value;

        return Result<McpServerDefinition>.Success(definition);
    }

    /// <summary>
    /// Reads the transport-specific fields into <paramref name="definition"/>: <c>url</c> for an http/sse
    /// entry, <c>command</c>/<c>args</c> for everything else (stdio). Mutates <paramref name="definition"/>
    /// in place rather than returning a value — the caller already owns it and every other field-reader in
    /// this class follows the same "read, then the caller decides whether to apply" shape except this one,
    /// which has two fields to set together and no natural single return value to carry them both.
    /// </summary>
    private static Result ReadTransportFields(
        JsonElement serverElement, McpServerType type, string serverName, McpServerDefinition definition)
    {
        if (type is McpServerType.Http or McpServerType.Sse)
        {
            var urlResult = ReadOptionalString(serverElement, "url", serverName);
            if (!urlResult.IsSuccess)
                return Result.Fail([.. urlResult.Errors]);

            if (string.IsNullOrWhiteSpace(urlResult.Value))
                return Result.Fail(
                    $"'{serverName}' declares a {type} transport with no 'url' — a remote server must declare one.");

            definition.Url = urlResult.Value;
            return Result.Success();
        }

        var commandResult = ReadOptionalString(serverElement, "command", serverName);
        if (!commandResult.IsSuccess)
            return Result.Fail([.. commandResult.Errors]);
        if (commandResult.Value is not null)
            definition.Command = commandResult.Value;

        var argsResult = ReadArgs(serverElement, serverName);
        if (!argsResult.IsSuccess)
            return Result.Fail([.. argsResult.Errors]);
        if (argsResult.Value is not null)
            definition.Args = argsResult.Value;

        return Result.Success();
    }

    /// <summary>
    /// Reads the optional <c>type</c> property, defaulting to <see cref="McpServerType.Stdio"/> — every
    /// manifest written before this branch existed omits the property and means stdio. Fails only if
    /// <c>type</c> is present with a non-string JSON shape; an unrecognized string value (not just an
    /// absent property) still defaults to stdio, matching the pre-existing, callers-depend-on-it behavior
    /// (see <c>BundleStagingService.LogStdioRejected</c>).
    /// </summary>
    private static Result<McpServerType> ParseType(JsonElement serverElement, string serverName) =>
        ReadOptionalString(serverElement, "type", serverName).Map(raw => ParseTypeValue(raw));

    /// <summary>Maps a raw, already-extracted <c>type</c> string the way <see cref="ParseType"/> does — the ONE mapping rule.</summary>
    private static McpServerType ParseTypeValue(string? raw) => raw?.ToLowerInvariant() switch
    {
        "http" => McpServerType.Http,
        "sse" => McpServerType.Sse,
        _ => McpServerType.Stdio
    };

    /// <summary>
    /// The manifest string <see cref="ParseTypeValue"/> recognizes for <paramref name="type"/> —
    /// deliberately NOT the inverse of the whole switch (its default arm maps every unrecognized string
    /// to <see cref="McpServerType.Stdio"/> too, which is exactly the ambiguity
    /// <see cref="IsExplicitType"/> exists to resolve; "recognized" and "defaulted-to" must stay
    /// distinguishable, not merged into one lookup).
    /// </summary>
    private static string? RecognizedTypeLiteral(McpServerType type) => type switch
    {
        McpServerType.Http => "http",
        McpServerType.Sse => "sse",
        McpServerType.Stdio => "stdio",
        _ => null
    };

    /// <summary>
    /// Whether a manifest declares <c>"type"</c> as the exact recognized string for
    /// <paramref name="type"/> — distinct from <see cref="ParseType"/>'s own default-to-stdio behavior
    /// for an absent or unrecognized value. <c>BundleStagingService.TryRegisterStdioServer</c> needs
    /// exactly this distinction for <see cref="McpServerType.Stdio"/> specifically: a bundle author's
    /// typo'd remote transport (<c>"type": "htp"</c>) must not silently land on a sandboxed process
    /// launch just because unrecognized values default to the same enum value an explicit
    /// <c>"stdio"</c> would. Shares <see cref="RecognizedTypeLiteral"/> with <see cref="ParseTypeValue"/>
    /// rather than a second, independently-written comparison, so the two mapping rules cannot drift —
    /// a caller that instead re-implemented "read type, compare case-insensitively" would have no
    /// build-time signal if this class's own recognized aliases ever changed.
    /// </summary>
    /// <remarks>
    /// Callers of this method are expected to have already called <see cref="Build"/> successfully on
    /// the same <paramref name="serverElement"/> — this method does not itself validate that <c>type</c>
    /// has a string shape (an absent/wrong-shaped property both resolve to <c>false</c> here, harmlessly),
    /// because <see cref="Build"/> already rejects that shape before any caller could reach this check.
    /// </remarks>
    public static bool IsExplicitType(JsonElement serverElement, McpServerType type) =>
        serverElement.TryGetProperty("type", out var value)
        && value.ValueKind == JsonValueKind.String
        && string.Equals(value.GetString()?.ToLowerInvariant(), RecognizedTypeLiteral(type), StringComparison.Ordinal);

    /// <summary>
    /// Reads an optional string property. Absent is success with a <see langword="null"/> value —
    /// callers decide what "not declared" means. Present but not a JSON string (or JSON null) is a
    /// failure: calling <see cref="JsonElement.GetString"/> on any other kind throws, and this is exactly
    /// the untrusted-input case that must degrade to a caller-safe <see cref="Result{T}"/> failure instead.
    /// </summary>
    private static Result<string?> ReadOptionalString(JsonElement parent, string propertyName, string serverName)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
            return Result<string?>.Success(null);

        if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            return Result<string?>.Fail(
                $"'{serverName}' declares '{propertyName}' as {value.ValueKind}, but a string was expected.");

        return Result<string?>.Success(value.GetString());
    }

    /// <summary>
    /// Reads the optional <c>args</c> array, failing if it is present but not a JSON array, or if any
    /// element is present but not a JSON string.
    /// </summary>
    private static Result<List<string>?> ReadArgs(JsonElement parent, string serverName)
    {
        if (!parent.TryGetProperty("args", out var args))
            return Result<List<string>?>.Success(null);

        if (args.ValueKind is not JsonValueKind.Array)
            return Result<List<string>?>.Fail(
                $"'{serverName}' declares 'args' as {args.ValueKind}, but an array was expected.");

        var result = new List<string>();
        foreach (var element in args.EnumerateArray())
        {
            var leaf = ReadStringLeaf(element, "an 'args' element", serverName);
            if (!leaf.IsSuccess)
                return Result<List<string>?>.Fail([.. leaf.Errors]);

            result.Add(leaf.Value!);
        }

        return Result<List<string>?>.Success(result);
    }

    /// <summary>
    /// Reads the optional <c>env</c> object, failing if it is present but not a JSON object, or if any
    /// property value is present but not a JSON string.
    /// </summary>
    private static Result<Dictionary<string, string>?> ReadEnv(JsonElement parent, string serverName)
    {
        if (!parent.TryGetProperty("env", out var env))
            return Result<Dictionary<string, string>?>.Success(null);

        if (env.ValueKind is not JsonValueKind.Object)
            return Result<Dictionary<string, string>?>.Fail(
                $"'{serverName}' declares 'env' as {env.ValueKind}, but an object was expected.");

        var result = new Dictionary<string, string>();
        foreach (var property in env.EnumerateObject())
        {
            var leaf = ReadStringLeaf(property.Value, $"'env.{property.Name}'", serverName);
            if (!leaf.IsSuccess)
                return Result<Dictionary<string, string>?>.Fail([.. leaf.Errors]);

            result[property.Name] = leaf.Value!;
        }

        return Result<Dictionary<string, string>?>.Success(result);
    }

    /// <summary>
    /// Validates that an already-resolved JSON leaf value — one array element of <c>args</c>, or one
    /// property value of <c>env</c> — is a string (or null), sharing the same kind-check
    /// <see cref="ReadOptionalString"/> applies at the object-property level. <paramref name="context"/>
    /// names the leaf's location for the error message (e.g. <c>"an 'args' element"</c> or
    /// <c>"'env.NAME'"</c>).
    /// </summary>
    private static Result<string> ReadStringLeaf(JsonElement element, string context, string serverName)
    {
        if (element.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            return Result<string>.Fail(
                $"'{serverName}' declares {context} as {element.ValueKind}, but a string was expected.");

        return Result<string>.Success(element.GetString() ?? string.Empty);
    }
}
