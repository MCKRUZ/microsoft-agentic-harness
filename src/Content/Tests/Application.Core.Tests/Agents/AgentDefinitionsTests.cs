using Application.Core.Agents;
using FluentAssertions;
using Xunit;

namespace Application.Core.Tests.Agents;

public class AgentDefinitionsTests
{
    [Fact]
    public void CreateResearchAgent_ReturnsValidContext_WithNameAndInstructions()
    {
        var context = AgentDefinitions.CreateResearchAgent();

        context.Name.Should().Be("ResearchAgent");
        context.Description.Should().NotBeNullOrWhiteSpace();
        context.Instruction.Should().NotBeNullOrWhiteSpace();
        context.AdditionalProperties.Should().ContainKey("agentType")
            .WhoseValue.Should().Be("standalone");
    }

    [Fact]
    public void CreateResearchAgent_StripsYamlFrontmatter_FromInstructions()
    {
        var context = AgentDefinitions.CreateResearchAgent();

        // Frontmatter should be stripped — instructions must not start with "---"
        context.Instruction.Should().NotStartWith("---");
        // Body content should be present
        context.Instruction.Should().Contain("research");
    }

    [Fact]
    public void CreateOrchestratorAgent_IncludesSubAgentNames_InInstructions()
    {
        var agents = new[] { "ResearchAgent", "CodeReviewAgent" };

        var context = AgentDefinitions.CreateOrchestratorAgent(agents);

        context.Name.Should().Be("OrchestratorAgent");
        context.Instruction.Should().Contain("- ResearchAgent");
        context.Instruction.Should().Contain("- CodeReviewAgent");
        context.Instruction.Should().Contain("Available Agents");
    }

    [Fact]
    public void CreateOrchestratorAgent_ReturnsOrchestratorCategory_InAdditionalProperties()
    {
        var context = AgentDefinitions.CreateOrchestratorAgent(["Agent1"]);

        context.AdditionalProperties.Should().ContainKey("agentType")
            .WhoseValue.Should().Be("orchestrator");
        context.AdditionalProperties.Should().ContainKey("category")
            .WhoseValue.Should().Be("orchestration");
    }
}
