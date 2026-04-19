using Domain.AI.MCP;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.MCP;

/// <summary>
/// Tests for <see cref="McpPrompt"/> and <see cref="McpPromptArgument"/> records.
/// </summary>
public sealed class McpPromptTests
{
    [Fact]
    public void McpPrompt_Constructor_SetsAllValues()
    {
        var args = new List<McpPromptArgument>
        {
            new("topic", "The topic to research", true),
            new("depth", "Analysis depth", false)
        };

        var prompt = new McpPrompt("research", "Conduct research on a topic", args);

        prompt.Name.Should().Be("research");
        prompt.Description.Should().Be("Conduct research on a topic");
        prompt.Arguments.Should().HaveCount(2);
    }

    [Fact]
    public void McpPrompt_Equality_SameValues_AreEqual()
    {
        var args = new List<McpPromptArgument> { new("x") };
        var p1 = new McpPrompt("test", "desc", args);
        var p2 = new McpPrompt("test", "desc", args);

        p1.Should().Be(p2);
    }

    [Fact]
    public void McpPromptArgument_Constructor_RequiredOnly()
    {
        var arg = new McpPromptArgument("topic");

        arg.Name.Should().Be("topic");
        arg.Description.Should().BeNull();
        arg.Required.Should().BeNull();
    }

    [Fact]
    public void McpPromptArgument_Constructor_AllParameters()
    {
        var arg = new McpPromptArgument("topic", "Subject to research", true);

        arg.Name.Should().Be("topic");
        arg.Description.Should().Be("Subject to research");
        arg.Required.Should().BeTrue();
    }

    [Fact]
    public void McpPromptArgument_Equality_SameValues_AreEqual()
    {
        var arg1 = new McpPromptArgument("x", "desc", false);
        var arg2 = new McpPromptArgument("x", "desc", false);

        arg1.Should().Be(arg2);
    }
}
