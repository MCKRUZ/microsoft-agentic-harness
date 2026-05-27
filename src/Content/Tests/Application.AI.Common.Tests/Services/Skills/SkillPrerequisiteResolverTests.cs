using Application.AI.Common.Models;
using Application.AI.Common.Services.Skills;
using Domain.AI.Skills;
using Domain.AI.Tools;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace Application.AI.Common.Tests.Services.Skills;

/// <summary>
/// Tests for <see cref="SkillPrerequisiteResolver"/> covering prerequisite map
/// construction from skills and resolved tools.
/// </summary>
public class SkillPrerequisiteResolverTests
{
    private readonly SkillPrerequisiteResolver _resolver = new();

    [Fact]
    public void BuildPrerequisiteMap_WithPrerequisites_MapsCorrectly()
    {
        var validate = new SkillDefinition
        {
            Id = "validate", Name = "validate",
            Instructions = "Validate",
            CompletionTool = "run_validation",
            AllowedTools = ["check_syntax", "run_validation"]
        };
        var deploy = new SkillDefinition
        {
            Id = "deploy", Name = "deploy",
            Instructions = "Deploy",
            Prerequisites = ["validate"],
            AllowedTools = ["deploy_execute"]
        };

        var resolvedTools = new List<AITool>
        {
            AIFunctionFactory.Create(() => "r", "check_syntax"),
            AIFunctionFactory.Create(() => "r", "run_validation"),
            AIFunctionFactory.Create(() => "r", "deploy_execute")
        };

        var map = _resolver.BuildPrerequisiteMap([validate, deploy], resolvedTools);

        map.HasAnyPrerequisites.Should().BeTrue();
        map.Skills.Should().ContainKey("deploy");
        map.Skills["deploy"].Prerequisites.Should().BeEquivalentTo(["validate"]);
        map.Skills["validate"].CompletionTool.Should().Be("run_validation");
        map.Skills["validate"].ToolNames.Should().BeEquivalentTo(["check_syntax", "run_validation"]);
        map.Skills["deploy"].ToolNames.Should().BeEquivalentTo(["deploy_execute"]);
    }

    [Fact]
    public void BuildPrerequisiteMap_NoPrerequisites_HasAnyPrerequisitesIsFalse()
    {
        var skill = new SkillDefinition
        {
            Id = "simple", Name = "simple", Instructions = "Test"
        };
        var resolvedTools = new List<AITool>
        {
            AIFunctionFactory.Create(() => "r", "some_tool")
        };

        var map = _resolver.BuildPrerequisiteMap([skill], resolvedTools);

        map.HasAnyPrerequisites.Should().BeFalse();
    }

    [Fact]
    public void BuildPrerequisiteMap_ToolDeclarations_MatchedAgainstResolvedTools()
    {
        var skill = new SkillDefinition
        {
            Id = "research", Name = "research",
            Instructions = "Research",
            ToolDeclarations = [
                new ToolDeclaration { Name = "search" },
                new ToolDeclaration { Name = "summarize" }
            ]
        };

        var resolvedTools = new List<AITool>
        {
            AIFunctionFactory.Create(() => "r", "search"),
            AIFunctionFactory.Create(() => "r", "unrelated")
        };

        var map = _resolver.BuildPrerequisiteMap([skill], resolvedTools);

        map.Skills["research"].ToolNames.Should().BeEquivalentTo(["search"]);
    }

    [Fact]
    public void BuildPrerequisiteMap_PreCreatedTools_MatchedByName()
    {
        var preTool = AIFunctionFactory.Create(() => "r", "pre_tool");
        var skill = new SkillDefinition
        {
            Id = "s1", Name = "s1",
            Instructions = "Test",
            Tools = [preTool]
        };

        var map = _resolver.BuildPrerequisiteMap([skill], [preTool]);

        map.Skills["s1"].ToolNames.Should().BeEquivalentTo(["pre_tool"]);
    }
}
