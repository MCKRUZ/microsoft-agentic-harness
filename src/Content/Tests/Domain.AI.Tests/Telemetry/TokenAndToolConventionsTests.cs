using Domain.AI.Telemetry.Conventions;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Telemetry;

/// <summary>
/// Tests for <see cref="TokenConventions"/> and <see cref="ToolConventions"/> constants.
/// </summary>
public sealed class TokenAndToolConventionsTests
{
    [Theory]
    [InlineData(TokenConventions.Input, "agent.tokens.input")]
    [InlineData(TokenConventions.Output, "agent.tokens.output")]
    [InlineData(TokenConventions.Total, "agent.tokens.total")]
    [InlineData(TokenConventions.BudgetLimit, "agent.tokens.budget_limit")]
    [InlineData(TokenConventions.BudgetUsed, "agent.tokens.budget_used")]
    [InlineData(TokenConventions.BudgetPercent, "agent.tokens.budget_pct")]
    [InlineData(TokenConventions.CacheRead, "agent.tokens.cache_read")]
    [InlineData(TokenConventions.CacheWrite, "agent.tokens.cache_write")]
    [InlineData(TokenConventions.CostEstimated, "agent.tokens.cost_estimated")]
    [InlineData(TokenConventions.CacheHitRate, "agent.tokens.cache_hit_rate")]
    public void TokenConventions_HaveExpectedValues(string actual, string expected)
    {
        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData(TokenConventions.GenAiInputTokens, "gen_ai.usage.input_tokens")]
    [InlineData(TokenConventions.GenAiOutputTokens, "gen_ai.usage.output_tokens")]
    [InlineData(TokenConventions.GenAiCacheReadTokens, "gen_ai.usage.cache_read_input_tokens")]
    [InlineData(TokenConventions.GenAiRequestModel, "gen_ai.request.model")]
    public void TokenConventions_GenAi_HaveExpectedValues(string actual, string expected)
    {
        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData(ToolConventions.Name, "agent.tool.name")]
    [InlineData(ToolConventions.Source, "agent.tool.source")]
    [InlineData(ToolConventions.Status, "agent.tool.status")]
    [InlineData(ToolConventions.ErrorType, "agent.tool.error_type")]
    [InlineData(ToolConventions.Duration, "agent.tool.duration")]
    [InlineData(ToolConventions.Invocations, "agent.tool.invocations")]
    [InlineData(ToolConventions.Errors, "agent.tool.errors")]
    public void ToolConventions_HaveExpectedValues(string actual, string expected)
    {
        actual.Should().Be(expected);
    }

    [Fact]
    public void ToolConventions_StatusValues_AreCorrect()
    {
        ToolConventions.StatusValues.Success.Should().Be("success");
        ToolConventions.StatusValues.Failure.Should().Be("failure");
        ToolConventions.StatusValues.Timeout.Should().Be("timeout");
    }

    [Fact]
    public void ToolConventions_SourceValues_AreCorrect()
    {
        ToolConventions.SourceValues.KeyedDI.Should().Be("keyed_di");
        ToolConventions.SourceValues.Mcp.Should().Be("mcp");
        ToolConventions.SourceValues.SemanticKernel.Should().Be("semantic_kernel");
    }

    [Fact]
    public void ToolConventions_MaxResultLength_Is4096()
    {
        ToolConventions.MaxResultLength.Should().Be(4096);
    }

    [Theory]
    [InlineData(ToolConventions.ExecuteToolOperation, "execute_tool")]
    [InlineData(ToolConventions.GenAiToolName, "gen_ai.tool.name")]
    [InlineData(ToolConventions.ResultCategory, "tool.result_category")]
    [InlineData(ToolConventions.InputHash, "tool.input_hash")]
    public void ToolConventions_CausalAttribution_HaveExpectedValues(string actual, string expected)
    {
        actual.Should().Be(expected);
    }
}
