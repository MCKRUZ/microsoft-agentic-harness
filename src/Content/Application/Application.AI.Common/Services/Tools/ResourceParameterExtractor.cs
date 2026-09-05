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

        if (!declaredByOperation.TryGetValue(operation, out var declaredParameters) || declaredParameters.Count == 0)
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
}
