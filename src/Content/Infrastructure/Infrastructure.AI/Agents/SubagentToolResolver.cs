using Application.AI.Common.Interfaces.Agents;
using Domain.AI.Agents;
using Microsoft.Extensions.AI;

namespace Infrastructure.AI.Agents;

/// <summary>
/// Resolves the tool pool for a subagent by filtering the parent's tools
/// through the subagent's allowlist and denylist configuration.
/// </summary>
public sealed class SubagentToolResolver : ISubagentToolResolver
{
    /// <inheritdoc />
    public IReadOnlyList<AITool> ResolveToolsForSubagent(
        SubagentDefinition definition,
        IReadOnlyList<AITool> parentTools)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(parentTools);

        IEnumerable<AITool> tools = definition.InheritParentTools
            ? parentTools
            : [];

        if (definition.ToolAllowlist is { } allowlist)
        {
            var allowed = new HashSet<string>(allowlist, StringComparer.OrdinalIgnoreCase);
            tools = tools.Where(t => allowed.Contains(GetToolName(t)));
        }

        if (definition.ToolDenylist is { } denylist)
        {
            var denied = new HashSet<string>(denylist, StringComparer.OrdinalIgnoreCase);
            tools = tools.Where(t => !denied.Contains(GetToolName(t)));
        }

        return tools.ToList().AsReadOnly();
    }

    /// <summary>
    /// Extracts the tool name from an <see cref="AITool"/> instance.
    /// Uses <see cref="AITool.Name"/> which is populated from the underlying
    /// <see cref="AIFunction"/> metadata.
    /// </summary>
    private static string GetToolName(AITool tool)
    {
        return tool.Name ?? string.Empty;
    }
}
