using Domain.Common.Workflow;

namespace Infrastructure.AI.Generators;

/// <summary>
/// Interface for generating markdown representation of workflow state.
/// </summary>
/// <remarks>
/// This abstraction allows markdown generation to be reused across
/// different state manager implementations (e.g., for decorator pattern).
/// </remarks>
public interface IStateMarkdownGenerator
{
    /// <summary>
    /// Generates a markdown representation of the workflow state.
    /// </summary>
    /// <param name="state">The workflow state to serialize</param>
    /// <returns>A markdown string with YAML frontmatter and formatted body</returns>
    string Generate(WorkflowState state);
}
