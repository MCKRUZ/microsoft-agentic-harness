using Domain.AI.Hooks;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Hooks;

/// <summary>
/// Tests for <see cref="HookExecutionContext"/> record — construction, defaults, equality.
/// </summary>
public sealed class HookExecutionContextTests
{
    [Fact]
    public void Constructor_WithRequiredEvent_SetsValue()
    {
        var ctx = new HookExecutionContext { Event = HookEvent.PreToolUse };

        ctx.Event.Should().Be(HookEvent.PreToolUse);
    }

    [Fact]
    public void Defaults_OptionalProperties_AreNull()
    {
        var ctx = new HookExecutionContext { Event = HookEvent.SessionStart };

        ctx.AgentId.Should().BeNull();
        ctx.ToolName.Should().BeNull();
        ctx.ToolParameters.Should().BeNull();
        ctx.ToolResult.Should().BeNull();
        ctx.TurnNumber.Should().BeNull();
        ctx.ConversationId.Should().BeNull();
    }

    [Fact]
    public void AllProperties_SetExplicitly_RetainValues()
    {
        var parameters = new Dictionary<string, object?> { ["path"] = "/file.txt" };

        var ctx = new HookExecutionContext
        {
            Event = HookEvent.PostToolUse,
            AgentId = "research-agent",
            ToolName = "file_system",
            ToolParameters = parameters,
            ToolResult = "File content here",
            TurnNumber = 5,
            ConversationId = "conv-abc"
        };

        ctx.Event.Should().Be(HookEvent.PostToolUse);
        ctx.AgentId.Should().Be("research-agent");
        ctx.ToolName.Should().Be("file_system");
        ctx.ToolParameters.Should().ContainKey("path");
        ctx.ToolResult.Should().Be("File content here");
        ctx.TurnNumber.Should().Be(5);
        ctx.ConversationId.Should().Be("conv-abc");
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var params1 = new Dictionary<string, object?> { ["key"] = "val" };
        var ctx1 = new HookExecutionContext
        {
            Event = HookEvent.PreTurn,
            AgentId = "a",
            ToolParameters = params1
        };
        var ctx2 = new HookExecutionContext
        {
            Event = HookEvent.PreTurn,
            AgentId = "a",
            ToolParameters = params1
        };

        ctx1.Should().Be(ctx2);
    }
}
