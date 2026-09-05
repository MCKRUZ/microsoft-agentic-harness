using Domain.AI.Sandbox;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// The single routine that turns a tool's own <c>ResourceParametersByOperation</c> declaration plus
/// one call's already-flattened parameters into a <see cref="ToolCallResourceRequest"/> — shared by
/// both entry points a tool call reaches governance from (#418): the agent-turn path
/// (<see cref="GovernedAIFunction"/>, which flattens its raw JSON arguments first) and the
/// direct-invoke HTTP path (<c>DirectToolInvoker</c>, whose parameters are already flat).
/// </summary>
public static class ResourceParameterExtractor
{
    /// <summary>
    /// Extracts the requested paths/hosts for one tool call.
    /// </summary>
    /// <param name="operation">The operation this call names, or <see langword="null"/> if unreadable.</param>
    /// <param name="parameters">The call's already-flattened parameters, or <see langword="null"/>.</param>
    /// <param name="resourceParametersByOperation">
    /// The tool's own declaration (<c>ITool.ResourceParametersByOperation</c>).
    /// </param>
    /// <returns>
    /// <see langword="null"/> when the tool declares nothing, or <paramref name="operation"/> could
    /// not be read — resource usage for this call is unknown, and <c>CapabilityEnforcer</c> must
    /// treat that as a reason to refuse when scoping is configured, not as "nothing to check".
    /// <see cref="ToolCallResourceRequest.Empty"/> when the tool declares resource parameters for
    /// OTHER operations but affirmatively declares none for this one. Otherwise the paths/hosts found
    /// among <paramref name="parameters"/> for this operation's declared keys.
    /// </returns>
    public static ToolCallResourceRequest? Extract(
        string? operation,
        IReadOnlyDictionary<string, object?>? parameters,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, ResourceParameterKind>>? resourceParametersByOperation)
    {
        if (resourceParametersByOperation is not { Count: > 0 } declaredByOperation)
            return null;

        if (operation is null)
            return null;

        if (!TryFindDeclaration(declaredByOperation, operation, out var declaredParameters) || declaredParameters.Count == 0)
            return ToolCallResourceRequest.Empty;

        var paths = new List<string>();
        var hosts = new List<string>();

        foreach (var (parameterName, kind) in declaredParameters)
        {
            if (parameters is null || !parameters.TryGetValue(parameterName, out var value))
                continue;

            if (value is not string { Length: > 0 } text)
                continue;

            switch (kind)
            {
                case ResourceParameterKind.Path:
                    paths.Add(text);
                    break;
                case ResourceParameterKind.Host:
                    hosts.Add(text);
                    break;
            }
        }

        return new ToolCallResourceRequest(paths, hosts);
    }

    /// <summary>
    /// Looks up <paramref name="operation"/> in <paramref name="declaredByOperation"/>, matching
    /// case-insensitively so a casing difference can never downgrade a genuinely-declared operation
    /// to "affirmatively nothing scoped" (<see cref="ToolCallResourceRequest.Empty"/>) — which
    /// <see cref="Application.AI.Common.Services.Sandbox.CapabilityEnforcer"/> treats as trusted and
    /// skips validating. Every other stage that admits an operation name — <c>AIToolConverter</c>'s
    /// operation validation and <c>DirectToolInvoker</c>'s — accepts it case-insensitively, and
    /// <c>FileSystemTool.ExecuteAsync</c> itself dispatches via <c>ToLowerInvariant()</c>; an
    /// ordinal-only lookup here was the one link in that chain that disagreed, letting
    /// <c>operation: "Read"</c> bypass a <c>DeniedPaths</c> configured for <c>"read"</c> entirely.
    /// </summary>
    private static bool TryFindDeclaration(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, ResourceParameterKind>> declaredByOperation,
        string operation,
        out IReadOnlyDictionary<string, ResourceParameterKind> declaredParameters)
    {
        if (declaredByOperation.TryGetValue(operation, out var exact))
        {
            declaredParameters = exact;
            return true;
        }

        foreach (var (key, value) in declaredByOperation)
        {
            if (string.Equals(key, operation, StringComparison.OrdinalIgnoreCase))
            {
                declaredParameters = value;
                return true;
            }
        }

        declaredParameters = new Dictionary<string, ResourceParameterKind>();
        return false;
    }
}
