using Domain.AI.Agents;
using Domain.AI.Permissions;
using FluentAssertions;
using Infrastructure.AI.Agents;
using Microsoft.Extensions.AI;
using Xunit;

namespace Infrastructure.AI.Tests.Agents;

public sealed class SubagentToolResolverTests
{
    private readonly SubagentToolResolver _resolver = new();

    private static IReadOnlyList<AITool> CreateToolSet(params string[] names)
    {
        return names.Select(name =>
            (AITool)AIFunctionFactory.Create(
                () => "result",
                new AIFunctionFactoryOptions { Name = name }))
            .ToList()
            .AsReadOnly();
    }

    [Fact]
    public void ResolveTools_InheritAll_ReturnsParentTools()
    {
        var parentTools = CreateToolSet("file_system", "web_fetch", "bash");
        var definition = new SubagentDefinition
        {
            AgentType = SubagentType.General,
            InheritParentTools = true,
            ToolAllowlist = null,
            ToolDenylist = null
        };

        var result = _resolver.ResolveToolsForSubagent(definition, parentTools);

        result.Should().HaveCount(3);
        result.Select(t => t.Name).Should().BeEquivalentTo("file_system", "web_fetch", "bash");
    }

    [Fact]
    public void ResolveTools_Allowlist_FiltersToAllowed()
    {
        var parentTools = CreateToolSet("file_system", "web_fetch", "bash", "git");
        var definition = new SubagentDefinition
        {
            AgentType = SubagentType.Explore,
            InheritParentTools = true,
            ToolAllowlist = ["file_system", "git"]
        };

        var result = _resolver.ResolveToolsForSubagent(definition, parentTools);

        result.Should().HaveCount(2);
        result.Select(t => t.Name).Should().BeEquivalentTo("file_system", "git");
    }

    [Fact]
    public void ResolveTools_Denylist_RemovesDenied()
    {
        var parentTools = CreateToolSet("file_system", "web_fetch", "bash");
        var definition = new SubagentDefinition
        {
            AgentType = SubagentType.General,
            InheritParentTools = true,
            ToolDenylist = ["bash"]
        };

        var result = _resolver.ResolveToolsForSubagent(definition, parentTools);

        result.Should().HaveCount(2);
        result.Select(t => t.Name).Should().BeEquivalentTo("file_system", "web_fetch");
    }

    [Fact]
    public void ResolveTools_AllowlistAndDenylist_AppliesBoth()
    {
        var parentTools = CreateToolSet("file_system", "web_fetch", "bash", "git");
        var definition = new SubagentDefinition
        {
            AgentType = SubagentType.General,
            InheritParentTools = true,
            ToolAllowlist = ["file_system", "bash", "git"],
            ToolDenylist = ["bash"]
        };

        var result = _resolver.ResolveToolsForSubagent(definition, parentTools);

        result.Should().HaveCount(2);
        result.Select(t => t.Name).Should().BeEquivalentTo("file_system", "git");
    }

    [Fact]
    public void ResolveTools_NoInherit_ReturnsEmpty()
    {
        var parentTools = CreateToolSet("file_system", "web_fetch", "bash");
        var definition = new SubagentDefinition
        {
            AgentType = SubagentType.Plan,
            InheritParentTools = false
        };

        var result = _resolver.ResolveToolsForSubagent(definition, parentTools);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ResolveTools_EmptyAllowlist_ReturnsEmpty()
    {
        var parentTools = CreateToolSet("file_system", "web_fetch", "bash");
        var definition = new SubagentDefinition
        {
            AgentType = SubagentType.Plan,
            InheritParentTools = true,
            ToolAllowlist = []
        };

        var result = _resolver.ResolveToolsForSubagent(definition, parentTools);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ResolveTools_CaseInsensitive_AllowlistMatching()
    {
        var parentTools = CreateToolSet("File_System", "Web_Fetch");
        var definition = new SubagentDefinition
        {
            AgentType = SubagentType.Explore,
            InheritParentTools = true,
            ToolAllowlist = ["file_system"]
        };

        var result = _resolver.ResolveToolsForSubagent(definition, parentTools);

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("File_System");
    }
}
