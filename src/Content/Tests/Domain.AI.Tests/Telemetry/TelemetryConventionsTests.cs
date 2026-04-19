using Domain.AI.Telemetry.Conventions;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Telemetry;

/// <summary>
/// Tests for all telemetry convention constants — verifies attribute names are non-empty
/// and follow the expected naming convention (dot-separated, lowercase).
/// </summary>
public sealed class TelemetryConventionsTests
{
    [Theory]
    [InlineData(AgentConventions.Name, "agent.name")]
    [InlineData(AgentConventions.ParentName, "agent.parent_agent.name")]
    [InlineData(AgentConventions.ConversationId, "agent.conversation.id")]
    [InlineData(AgentConventions.TurnIndex, "agent.turn.index")]
    [InlineData(AgentConventions.TurnRole, "agent.turn.role")]
    [InlineData(AgentConventions.Phase, "agent.phase")]
    [InlineData(AgentConventions.GenAiSystem, "gen_ai.system")]
    public void AgentConventions_HaveExpectedValues(string actual, string expected)
    {
        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData(CompactionConventions.Strategy, "agent.compaction.strategy")]
    [InlineData(CompactionConventions.Trigger, "agent.compaction.trigger")]
    [InlineData(CompactionConventions.TokensSaved, "agent.compaction.tokens_saved")]
    [InlineData(CompactionConventions.Duration, "agent.compaction.duration_ms")]
    [InlineData(CompactionConventions.CircuitBroken, "agent.compaction.circuit_broken")]
    public void CompactionConventions_HaveExpectedValues(string actual, string expected)
    {
        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData(ContextConventions.BudgetLimit, "agent.context.budget_limit")]
    [InlineData(ContextConventions.BudgetUsed, "agent.context.budget_used")]
    [InlineData(ContextConventions.SystemPromptTokens, "agent.context.system_prompt_tokens")]
    [InlineData(ContextConventions.BudgetRemaining, "agent.context.budget_remaining")]
    [InlineData(ContextConventions.BudgetUtilization, "agent.context.budget_utilization")]
    public void ContextConventions_HaveExpectedValues(string actual, string expected)
    {
        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData(HookConventions.EventType, "agent.hook.event")]
    [InlineData(HookConventions.HookType, "agent.hook.type")]
    [InlineData(HookConventions.HookId, "agent.hook.id")]
    [InlineData(HookConventions.Duration, "agent.hook.duration_ms")]
    [InlineData(HookConventions.Continued, "agent.hook.continued")]
    public void HookConventions_HaveExpectedValues(string actual, string expected)
    {
        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData(McpConventions.ServerName, "mcp.server.name")]
    [InlineData(McpConventions.Operation, "mcp.server.operation")]
    [InlineData(McpConventions.Status, "mcp.server.status")]
    [InlineData(McpConventions.ErrorType, "mcp.server.error_type")]
    public void McpConventions_HaveExpectedValues(string actual, string expected)
    {
        actual.Should().Be(expected);
    }

    [Fact]
    public void McpConventions_StatusValues_AreCorrect()
    {
        McpConventions.StatusValues.Available.Should().Be("available");
        McpConventions.StatusValues.Unavailable.Should().Be("unavailable");
        McpConventions.StatusValues.Error.Should().Be("error");
    }

    [Theory]
    [InlineData(OrchestrationConventions.TurnCount, "agent.orchestration.turn_count")]
    [InlineData(OrchestrationConventions.SubagentCount, "agent.orchestration.subagent_count")]
    [InlineData(OrchestrationConventions.ToolCallCount, "agent.orchestration.tool_call_count")]
    public void OrchestrationConventions_HaveExpectedValues(string actual, string expected)
    {
        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData(PermissionConventions.Decision, "agent.permission.decision")]
    [InlineData(PermissionConventions.RuleSource, "agent.permission.source")]
    [InlineData(PermissionConventions.ToolName, "agent.permission.tool")]
    [InlineData(PermissionConventions.DenialCount, "agent.permission.denials")]
    public void PermissionConventions_HaveExpectedValues(string actual, string expected)
    {
        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData(SafetyConventions.Phase, "agent.safety.phase")]
    [InlineData(SafetyConventions.Filter, "agent.safety.filter")]
    [InlineData(SafetyConventions.Outcome, "agent.safety.outcome")]
    [InlineData(SafetyConventions.Category, "agent.safety.category")]
    public void SafetyConventions_HaveExpectedValues(string actual, string expected)
    {
        actual.Should().Be(expected);
    }

    [Fact]
    public void SafetyConventions_PhaseValues_AreCorrect()
    {
        SafetyConventions.PhaseValues.Prompt.Should().Be("prompt");
        SafetyConventions.PhaseValues.Response.Should().Be("response");
    }

    [Fact]
    public void SafetyConventions_OutcomeValues_AreCorrect()
    {
        SafetyConventions.OutcomeValues.Pass.Should().Be("pass");
        SafetyConventions.OutcomeValues.Block.Should().Be("block");
        SafetyConventions.OutcomeValues.Redact.Should().Be("redact");
    }

    [Fact]
    public void SafetyConventions_CategoryValues_AreCorrect()
    {
        SafetyConventions.CategoryValues.Hate.Should().Be("hate");
        SafetyConventions.CategoryValues.Violence.Should().Be("violence");
        SafetyConventions.CategoryValues.SelfHarm.Should().Be("self-harm");
        SafetyConventions.CategoryValues.Sexual.Should().Be("sexual");
        SafetyConventions.CategoryValues.Pii.Should().Be("pii");
        SafetyConventions.CategoryValues.Jailbreak.Should().Be("jailbreak");
        SafetyConventions.CategoryValues.PromptInjection.Should().Be("prompt-injection");
    }
}
