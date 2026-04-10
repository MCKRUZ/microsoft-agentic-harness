using System.ComponentModel;
using System.Text.Json;
using Application.AI.Common.Interfaces;
using ModelContextProtocol.Server;

namespace Infrastructure.AI.MCPServer.Tools;

/// <summary>
/// MCP tools for querying the skill catalog. Allows external MCP clients
/// (Claude Desktop, Claude Code, other agents) to discover and inspect
/// the available skills in this harness.
/// </summary>
[McpServerToolType]
public sealed class SkillTools(ISkillMetadataRegistry registry)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Lists all available skills, optionally filtered by category.
    /// </summary>
    [McpServerTool(Name = "list_skills")]
    [Description("Lists all available agent skills. Returns id, name, description, category, and tags for each skill. Optionally filter by category (e.g. 'research', 'orchestration').")]
    public string ListSkills(
        [Description("Optional category to filter by (e.g. 'research', 'orchestration'). Omit to list all skills.")]
        string? category = null)
    {
        var skills = string.IsNullOrWhiteSpace(category)
            ? registry.GetAll()
            : registry.GetByCategory(category);

        var summaries = skills.Select(s => new
        {
            id = s.Id,
            name = s.Name,
            description = s.Description,
            category = s.Category,
            tags = s.Tags,
            version = s.Version
        });

        return JsonSerializer.Serialize(summaries, JsonOptions);
    }

    /// <summary>
    /// Returns the full details and instructions for a specific skill.
    /// </summary>
    [McpServerTool(Name = "get_skill")]
    [Description("Gets the full details and system instructions for a specific skill by ID. Use list_skills first to discover available skill IDs.")]
    public string GetSkill(
        [Description("The skill ID to retrieve (e.g. 'research-agent', 'orchestrator-agent').")]
        string skillId)
    {
        var skill = registry.TryGet(skillId);

        if (skill is null)
        {
            var available = string.Join(", ", registry.GetAll().Select(s => s.Id));
            return JsonSerializer.Serialize(new
            {
                error = $"Skill '{skillId}' not found.",
                availableSkills = available
            }, JsonOptions);
        }

        return JsonSerializer.Serialize(new
        {
            id = skill.Id,
            name = skill.Name,
            description = skill.Description,
            category = skill.Category,
            tags = skill.Tags,
            version = skill.Version,
            allowedTools = skill.AllowedTools,
            instructions = skill.Instructions
        }, JsonOptions);
    }

    /// <summary>
    /// Searches for skills by one or more tags.
    /// </summary>
    [McpServerTool(Name = "find_skills_by_tag")]
    [Description("Finds skills that have any of the specified tags. Returns a list of matching skills.")]
    public string FindSkillsByTag(
        [Description("Comma-separated list of tags to search for (e.g. 'orchestrator,multi-agent').")]
        string tags)
    {
        var tagList = tags
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var skills = registry.GetByTags(tagList);

        var summaries = skills.Select(s => new
        {
            id = s.Id,
            name = s.Name,
            description = s.Description,
            category = s.Category,
            tags = s.Tags
        });

        return JsonSerializer.Serialize(summaries, JsonOptions);
    }
}
