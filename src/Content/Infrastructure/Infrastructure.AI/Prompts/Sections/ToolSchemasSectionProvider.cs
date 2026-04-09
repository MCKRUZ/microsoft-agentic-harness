using System.Text;
using Application.AI.Common.Helpers;
using Application.AI.Common.Interfaces.Prompts;
using Application.AI.Common.Interfaces.Tools;
using Domain.AI.Prompts;

namespace Infrastructure.AI.Prompts.Sections;

/// <summary>
/// Provides the tool schemas section — lists available tool names and descriptions
/// for agent awareness. Cacheable because the tool set typically does not change
/// within a conversation.
/// </summary>
/// <remarks>
/// Tools are injected as <c>IEnumerable&lt;ITool&gt;</c>. For this to work, tools must
/// be registered as both keyed singletons (for resolution by name) and as non-keyed
/// <c>ITool</c> services (for enumeration). If no tools are registered as non-keyed
/// services, the section returns <c>null</c>.
/// </remarks>
public sealed class ToolSchemasSectionProvider : IPromptSectionProvider
{
    private readonly IReadOnlyList<ITool> _tools;

    /// <summary>
    /// Initializes a new instance of <see cref="ToolSchemasSectionProvider"/>.
    /// </summary>
    /// <param name="tools">All registered tool implementations.</param>
    public ToolSchemasSectionProvider(IEnumerable<ITool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _tools = tools.ToList();
    }

    /// <inheritdoc />
    public SystemPromptSectionType SectionType => SystemPromptSectionType.ToolSchemas;

    /// <inheritdoc />
    public Task<SystemPromptSection?> GetSectionAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        if (_tools.Count == 0)
            return Task.FromResult<SystemPromptSection?>(null);

        var builder = new StringBuilder();
        builder.AppendLine("# Available Tools");
        builder.AppendLine();

        foreach (var tool in _tools)
        {
            builder.AppendLine($"- **{tool.Name}**: {tool.Description}");
        }

        var content = builder.ToString().TrimEnd();

        var section = new SystemPromptSection(
            Name: "Tool Schemas",
            Type: SystemPromptSectionType.ToolSchemas,
            Priority: 30,
            IsCacheable: true,
            EstimatedTokens: TokenEstimationHelper.EstimateTokens(content),
            Content: content);

        return Task.FromResult<SystemPromptSection?>(section);
    }
}
