using Domain.Common.Workflow;
using System.Text;

namespace Infrastructure.AI.Generators;

/// <summary>
/// Default implementation of markdown generator for workflow state.
/// </summary>
/// <remarks>
/// Generates markdown with YAML frontmatter containing key workflow information,
/// followed by a human-readable markdown body with node details.
/// <para><b>Format:</b></para>
/// <code>
/// ---
/// workflow_id: proj-001
/// current_node_id: phase0-discovery
/// workflow_status: in_progress
/// ---
///
/// # Workflow State: proj-001
///
/// **Current Node**: phase0-discovery
/// **Status**: in_progress
///
/// ## Nodes
/// ...
/// </code>
/// </remarks>
public class StateMarkdownGenerator : IStateMarkdownGenerator
{
    /// <summary>
    /// Generates a markdown representation of the workflow state.
    /// </summary>
    public string Generate(WorkflowState state)
    {
        var sb = new StringBuilder();

        // YAML frontmatter
        sb.AppendLine("---");
        sb.AppendLine($"workflow_id: {state.WorkflowId}");
        sb.AppendLine($"current_node_id: {state.CurrentNodeId}");
        sb.AppendLine($"workflow_status: {state.WorkflowStatus}");
        sb.AppendLine($"workflow_started: {state.WorkflowStarted:yyyy-MM-ddTHH:mm:ssZ}");
        if (state.WorkflowCompleted.HasValue)
            sb.AppendLine($"workflow_completed: {state.WorkflowCompleted.Value:yyyy-MM-ddTHH:mm:ssZ}");
        sb.AppendLine("---");
        sb.AppendLine();

        // Markdown body
        sb.AppendLine($"# Workflow State: {state.WorkflowId}");
        sb.AppendLine();
        sb.AppendLine($"**Current Node**: {state.CurrentNodeId}");
        sb.AppendLine($"**Status**: {state.WorkflowStatus}");
        sb.AppendLine($"**Started**: {state.WorkflowStarted:yyyy-MM-ddTHH:mm:ssZ}");
        if (state.WorkflowCompleted.HasValue)
            sb.AppendLine($"**Completed**: {state.WorkflowCompleted.Value:yyyy-MM-ddTHH:mm:ssZ}");
        sb.AppendLine();

        // Nodes section
        if (state.Nodes.Count > 0)
        {
            sb.AppendLine("## Nodes");
            sb.AppendLine();

            foreach (var (nodeId, node) in state.Nodes.OrderBy(n => n.Value.StartedAt ?? DateTime.MaxValue))
            {
                sb.AppendLine($"### {nodeId} ({node.NodeType})");
                sb.AppendLine($"- **Status**: {node.Status}");

                if (node.StartedAt.HasValue)
                    sb.AppendLine($"- **Started**: {node.StartedAt.Value:yyyy-MM-ddTHH:mm:ssZ}");

                if (node.CompletedAt.HasValue)
                    sb.AppendLine($"- **Completed**: {node.CompletedAt.Value:yyyy-MM-ddTHH:mm:ssZ}");

                sb.AppendLine($"- **Iteration**: {node.Iteration}");

                if (node.Metadata.Count > 0)
                {
                    sb.AppendLine("- **Metadata**:");
                    foreach (var (key, value) in node.Metadata.OrderBy(m => m.Key))
                    {
                        sb.AppendLine($"  - {key}: {FormatMetadataValue(value)}");
                    }
                }

                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Formats a metadata value for markdown output.
    /// </summary>
    private static string FormatMetadataValue(object value)
    {
        return value switch
        {
            string s => $"\"{s}\"",
            bool b => b.ToString().ToLowerInvariant(),
            null => "null",
            _ => value.ToString() ?? value.GetType().Name
        };
    }
}
