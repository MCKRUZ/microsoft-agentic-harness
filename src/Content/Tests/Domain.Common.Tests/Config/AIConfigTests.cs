using Domain.Common.Config.AI;
using FluentAssertions;
using Xunit;

namespace Domain.Common.Tests.Config;

/// <summary>
/// Tests for <see cref="AIConfig"/>, <see cref="AgentFrameworkConfig"/>,
/// <see cref="AIAgentFrameworkClientType"/>, and <see cref="SkillResourceType"/> enums.
/// </summary>
public class AIConfigTests
{
    [Fact]
    public void DefaultValues_AllSubsectionsInitialized()
    {
        var config = new AIConfig();

        config.AgentFramework.Should().NotBeNull();
        config.AIFoundry.Should().NotBeNull();
        config.MCP.Should().NotBeNull();
        config.McpServers.Should().NotBeNull();
        config.A2A.Should().NotBeNull();
        config.ContextManagement.Should().NotBeNull();
        config.Permissions.Should().NotBeNull();
        config.Hooks.Should().NotBeNull();
        config.Orchestration.Should().NotBeNull();
        config.Skills.Should().NotBeNull();
        config.Agents.Should().NotBeNull();
    }
}

/// <summary>
/// Tests for <see cref="AgentFrameworkConfig"/> defaults and IsConfigured logic.
/// </summary>
public class AgentFrameworkConfigTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var config = new AgentFrameworkConfig();

        config.Endpoint.Should().BeNull();
        config.ApiKey.Should().BeNull();
        config.DefaultDeployment.Should().Be("default");
        config.AvailableDeployments.Should().BeEmpty();
        config.ClientType.Should().Be(AIAgentFrameworkClientType.AzureOpenAI);
    }

    [Fact]
    public void ConversationTokenBudget_Unconfigured_DefaultsToOneMillion()
    {
        // A durable conversation outlives any single run, so the per-run caps do not bound it — the
        // Execution API contract names this budget as the only thing that does. At 0 the tracker reports
        // Disabled and records nothing, so that documented bound silently ceases to exist; tuning the
        // figure is fine, returning it to 0 is not. The figure itself is roughly 50-100 exchanges once
        // each turn's resent history is counted, and is pinned so that changing it is a deliberate edit
        // with this test in the diff.
        new AgentFrameworkConfig().ConversationTokenBudget.Should().Be(
            1_000_000,
            "a conversation with no ceiling has nothing else bounding its total length");
    }

    [Fact]
    public void IsConfigured_WithNoApiKey_ReturnsFalse()
    {
        var config = new AgentFrameworkConfig();

        config.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_WithApiKey_ReturnsTrue()
    {
        var config = new AgentFrameworkConfig { ApiKey = "sk-test-key" };

        config.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void IsConfigured_WithWhitespaceApiKey_ReturnsFalse()
    {
        var config = new AgentFrameworkConfig { ApiKey = "  " };

        config.IsConfigured.Should().BeFalse();
    }
}

/// <summary>
/// Tests for <see cref="AIAgentFrameworkClientType"/> enum values.
/// </summary>
public class AIAgentFrameworkClientTypeTests
{
    [Theory]
    [InlineData(AIAgentFrameworkClientType.AzureOpenAI, 0)]
    [InlineData(AIAgentFrameworkClientType.OpenAI, 1)]
    [InlineData(AIAgentFrameworkClientType.AzureAIInference, 2)]
    [InlineData(AIAgentFrameworkClientType.PersistentAgents, 3)]
    [InlineData(AIAgentFrameworkClientType.Anthropic, 4)]
    [InlineData(AIAgentFrameworkClientType.Echo, 5)]
    [InlineData(AIAgentFrameworkClientType.FoundryResponses, 6)]
    public void Value_HasExpectedInteger(AIAgentFrameworkClientType type, int expected)
    {
        ((int)type).Should().Be(expected);
    }

    [Fact]
    public void AllValues_AreDistinct()
    {
        Enum.GetValues<AIAgentFrameworkClientType>().Should().OnlyHaveUniqueItems();
        Enum.GetValues<AIAgentFrameworkClientType>().Should().HaveCount(7);
    }

    [Fact]
    public void Parse_FromString_Works()
    {
        Enum.Parse<AIAgentFrameworkClientType>("Anthropic")
            .Should().Be(AIAgentFrameworkClientType.Anthropic);
    }
}

/// <summary>
/// Tests for <see cref="SkillResourceType"/> enum values.
/// </summary>
public class SkillResourceTypeTests
{
    [Theory]
    [InlineData(SkillResourceType.Template, 0)]
    [InlineData(SkillResourceType.Reference, 1)]
    [InlineData(SkillResourceType.Script, 2)]
    [InlineData(SkillResourceType.Asset, 3)]
    public void Value_HasExpectedInteger(SkillResourceType type, int expected)
    {
        ((int)type).Should().Be(expected);
    }

    [Fact]
    public void AllValues_AreDistinct()
    {
        Enum.GetValues<SkillResourceType>().Should().OnlyHaveUniqueItems();
        Enum.GetValues<SkillResourceType>().Should().HaveCount(4);
    }
}
