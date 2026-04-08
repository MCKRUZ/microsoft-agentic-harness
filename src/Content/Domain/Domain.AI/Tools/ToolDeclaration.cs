namespace Domain.AI.Tools;

/// <summary>
/// Represents a tool declaration from SKILL.md YAML frontmatter.
/// Defines which tool is needed, allowed operations, fallback behavior, and usage guidance.
/// </summary>
/// <remarks>
/// <para><b>Example from SKILL.md:</b></para>
/// <code>
/// tools:
///   - name: azure_devops_work_items
///     operations:
///       - create_sprint
///       - create_work_item
///     fallback: jira_issues
///     optional: true
/// </code>
/// </remarks>
public class ToolDeclaration
{
	/// <summary>
	/// Tool name that must match a registered tool in keyed DI or an MCP server.
	/// </summary>
	public string Name { get; set; } = string.Empty;

	/// <summary>
	/// Specific operations this skill will use from the tool.
	/// If empty, all operations are allowed.
	/// </summary>
	public List<string> Operations { get; set; } = [];

	/// <summary>
	/// Whether this tool is optional (skill can function without it).
	/// </summary>
	public bool Optional { get; set; }

	/// <summary>
	/// Fallback tool to use if this tool is unavailable.
	/// Can be another tool name or "manual".
	/// </summary>
	public string? Fallback { get; set; }

	/// <summary>
	/// Condition under which this tool is needed.
	/// </summary>
	public string? Condition { get; set; }

	/// <summary>
	/// Additional metadata about the tool requirement.
	/// </summary>
	public Dictionary<string, object>? Metadata { get; set; }

	/// <summary>
	/// Human-readable description of what this tool is used for in this skill.
	/// </summary>
	public string Description { get; set; } = string.Empty;

	/// <summary>
	/// Guidance for when this tool should be used.
	/// </summary>
	public string WhenToUse { get; set; } = string.Empty;

	/// <summary>
	/// Guidance for when this tool should NOT be used.
	/// </summary>
	public string WhenNotToUse { get; set; } = string.Empty;

	/// <summary>
	/// Whether this tool has a fallback option.
	/// </summary>
	public bool HasFallback => !string.IsNullOrWhiteSpace(Fallback);

	/// <summary>
	/// Whether the fallback is manual (not another tool).
	/// </summary>
	public bool FallbackIsManual => Fallback?.Equals("manual", StringComparison.OrdinalIgnoreCase) ?? false;

	/// <summary>
	/// Whether this tool has specific operations defined.
	/// </summary>
	public bool HasOperations => Operations.Count > 0;
}
