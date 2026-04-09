using Domain.AI.Hooks;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Hooks;

public class HookDefinitionTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var hook = new HookDefinition
        {
            Id = "test-hook",
            Event = HookEvent.PreToolUse,
            Type = HookType.Prompt
        };

        hook.TimeoutMs.Should().Be(5000);
        hook.Priority.Should().Be(100);
        hook.RunOnce.Should().BeFalse();
        hook.ToolMatcher.Should().BeNull();
        hook.CommandLine.Should().BeNull();
        hook.PromptTemplate.Should().BeNull();
        hook.MiddlewareTypeName.Should().BeNull();
        hook.WebhookUrl.Should().BeNull();
    }

    [Fact]
    public void HookResult_PassThrough_DefaultsContinueTrue()
    {
        var result = HookResult.PassThrough();

        result.Continue.Should().BeTrue();
        result.SuppressOutput.Should().BeFalse();
        result.ModifiedInput.Should().BeNull();
        result.ModifiedOutput.Should().BeNull();
        result.AdditionalContext.Should().BeNull();
        result.StopReason.Should().BeNull();
    }

    [Fact]
    public void HookResult_Block_SetsStopReason()
    {
        var reason = "Tool is not allowed in this context";
        var result = HookResult.Block(reason);

        result.Continue.Should().BeFalse();
        result.StopReason.Should().Be(reason);
    }
}
