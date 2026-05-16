namespace Application.AI.Common.Interfaces.MediatR;

/// <summary>
/// Extended tool request declaring filesystem paths and network hosts the tool intends to access.
/// Consumed by capability enforcement to validate resource scoping against the tool's permission profile.
/// Tools that don't implement this interface skip path/host validation.
/// </summary>
public interface IResourceScopedToolRequest : IToolRequest
{
    /// <summary>Filesystem paths the tool wants to access during this invocation.</summary>
    IReadOnlyList<string>? RequestedPaths { get; }

    /// <summary>Network hosts the tool wants to contact during this invocation.</summary>
    IReadOnlyList<string>? RequestedHosts { get; }
}
