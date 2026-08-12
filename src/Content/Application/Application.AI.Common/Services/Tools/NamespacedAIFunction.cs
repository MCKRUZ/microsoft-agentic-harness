using Microsoft.Extensions.AI;

namespace Application.AI.Common.Services.Tools;

/// <summary>
/// Wraps a tool resolved via a bundle's own MCP server so it is published to the model and governed
/// under its namespaced name (<see cref="BundleOwnedMcpToolNaming"/>) rather than the bare name the
/// (untrusted, bundle-authored) server itself declared. Derives from <see cref="DelegatingAIFunction"/>
/// so invocation, description, and schema all still delegate to the inner function — only the name
/// changes, which is exactly what the invocation-time governance gate matches on.
/// </summary>
internal sealed class NamespacedAIFunction : DelegatingAIFunction
{
    /// <summary>Wraps <paramref name="innerFunction"/> under <paramref name="namespacedName"/>.</summary>
    public NamespacedAIFunction(AIFunction innerFunction, string namespacedName)
        : base(innerFunction)
    {
        Name = namespacedName;
    }

    /// <inheritdoc />
    public override string Name { get; }
}
