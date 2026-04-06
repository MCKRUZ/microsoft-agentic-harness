namespace Application.AI.Common.Interfaces.MediatR;

/// <summary>
/// Marker interface for requests that represent a tool invocation.
/// Consumed by <c>ToolPermissionBehavior</c> to check whether the current
/// agent is allowed to use the specified tool.
/// </summary>
public interface IToolRequest
{
    /// <summary>Gets the tool name or key (e.g., "file_system", "web_fetch").</summary>
    string ToolName { get; }
}
