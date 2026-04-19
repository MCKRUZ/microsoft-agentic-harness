using Application.Core.Agents;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.Agents;

/// <summary>
/// Extended tests for <see cref="AgentDefinitions"/>, covering custom deployment names,
/// empty agent lists, and additional property verification.
/// </summary>
public class AgentDefinitionsTests_Extended
{
    [Fact]
    public void CreateResearchAgent_WithCustomDeployment_SetsDeploymentName()
    {
        var context = AgentDefinitions.CreateResearchAgent("gpt-4o-mini");

        context.DeploymentName.Should().Be("gpt-4o-mini");
    }

    [Fact]
    public void CreateResearchAgent_WithNullDeployment_DeploymentIsNull()
    {
        var context = AgentDefinitions.CreateResearchAgent(null);

        context.DeploymentName.Should().BeNull();
    }

    [Fact]
    public void CreateResearchAgent_WithoutDeployment_DefaultDeploymentIsNull()
    {
        var context = AgentDefinitions.CreateResearchAgent();

        context.DeploymentName.Should().BeNull();
    }

    [Fact]
    public void CreateResearchAgent_HasResearchCategory()
    {
        var context = AgentDefinitions.CreateResearchAgent();

        context.AdditionalProperties.Should().ContainKey("category")
            .WhoseValue.Should().Be("research");
    }

    [Fact]
    public void CreateResearchAgent_HasNonEmptyDescription()
    {
        var context = AgentDefinitions.CreateResearchAgent();

        context.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void CreateOrchestratorAgent_WithCustomDeployment_SetsDeploymentName()
    {
        var context = AgentDefinitions.CreateOrchestratorAgent(
            ["Agent1"], "claude-3-opus");

        context.DeploymentName.Should().Be("claude-3-opus");
    }

    [Fact]
    public void CreateOrchestratorAgent_WithNullDeployment_DeploymentIsNull()
    {
        var context = AgentDefinitions.CreateOrchestratorAgent(
            ["Agent1"], null);

        context.DeploymentName.Should().BeNull();
    }

    [Fact]
    public void CreateOrchestratorAgent_EmptyAgentList_HasEmptyAvailableAgentsSection()
    {
        var context = AgentDefinitions.CreateOrchestratorAgent([]);

        context.Name.Should().Be("OrchestratorAgent");
        context.Instruction.Should().Contain("Available Agents");
    }

    [Fact]
    public void CreateOrchestratorAgent_MultipleAgents_ListsAllInInstructions()
    {
        var agents = new[] { "Agent1", "Agent2", "Agent3", "Agent4" };

        var context = AgentDefinitions.CreateOrchestratorAgent(agents);

        foreach (var agent in agents)
        {
            context.Instruction.Should().Contain($"- {agent}");
        }
    }

    [Fact]
    public void CreateOrchestratorAgent_HasNonEmptyDescription()
    {
        var context = AgentDefinitions.CreateOrchestratorAgent(["A"]);

        context.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void CreateResearchAgent_InstructionsDoNotContainFrontmatterDelimiter()
    {
        var context = AgentDefinitions.CreateResearchAgent();

        context.Instruction.Should().NotContain("---");
    }

    [Fact]
    public void CreateOrchestratorAgent_InstructionsDoNotContainFrontmatterDelimiter()
    {
        var context = AgentDefinitions.CreateOrchestratorAgent(["A"]);

        // The base instructions should not contain raw frontmatter.
        // Agent list section is injected separately.
        context.Instruction.Should().NotStartWith("---");
    }
}
