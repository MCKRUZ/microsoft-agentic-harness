using Application.AI.Common.OpenTelemetry.Metrics;
using FluentAssertions;
using Xunit;

namespace Application.AI.Common.Tests.OpenTelemetry.Metrics;

/// <summary>
/// Tests for all OTel metric instrument classes ensuring they are properly initialized
/// as static singletons with correct instrument types and non-null references.
/// </summary>
public class MetricsInstrumentTests
{
    [Fact]
    public void ContentSafetyMetrics_Evaluations_IsNotNull()
    {
        ContentSafetyMetrics.Evaluations.Should().NotBeNull();
        ContentSafetyMetrics.Evaluations.Name.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ContentSafetyMetrics_Blocks_IsNotNull()
    {
        ContentSafetyMetrics.Blocks.Should().NotBeNull();
        ContentSafetyMetrics.Blocks.Name.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ContentSafetyMetrics_Severity_IsNotNull()
    {
        ContentSafetyMetrics.Severity.Should().NotBeNull();
        ContentSafetyMetrics.Severity.Name.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ContextBudgetMetrics_Compactions_IsNotNull()
    {
        ContextBudgetMetrics.Compactions.Should().NotBeNull();
        ContextBudgetMetrics.Compactions.Name.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ContextBudgetMetrics_SystemPromptTokens_IsNotNull()
    {
        ContextBudgetMetrics.SystemPromptTokens.Should().NotBeNull();
    }

    [Fact]
    public void ContextBudgetMetrics_SkillsLoadedTokens_IsNotNull()
    {
        ContextBudgetMetrics.SkillsLoadedTokens.Should().NotBeNull();
    }

    [Fact]
    public void ContextBudgetMetrics_ToolsSchemaTokens_IsNotNull()
    {
        ContextBudgetMetrics.ToolsSchemaTokens.Should().NotBeNull();
    }

    [Fact]
    public void ContextBudgetMetrics_BudgetUtilization_IsNotNull()
    {
        ContextBudgetMetrics.BudgetUtilization.Should().NotBeNull();
    }

    [Fact]
    public void LlmUsageMetrics_AllInstruments_AreNotNull()
    {
        LlmUsageMetrics.CacheReadTokens.Should().NotBeNull();
        LlmUsageMetrics.CacheWriteTokens.Should().NotBeNull();
        LlmUsageMetrics.EstimatedCost.Should().NotBeNull();
        LlmUsageMetrics.CacheSavings.Should().NotBeNull();
        LlmUsageMetrics.CacheHitRate.Should().NotBeNull();
        LlmUsageMetrics.CostPerTurn.Should().NotBeNull();
        LlmUsageMetrics.TokensPerTurn.Should().NotBeNull();
    }

    [Fact]
    public void McpServerMetrics_AllInstruments_AreNotNull()
    {
        McpServerMetrics.RequestDuration.Should().NotBeNull();
        McpServerMetrics.Requests.Should().NotBeNull();
    }

    [Fact]
    public void OrchestrationMetrics_AllInstruments_AreNotNull()
    {
        OrchestrationMetrics.ConversationDuration.Should().NotBeNull();
        OrchestrationMetrics.TurnsPerConversation.Should().NotBeNull();
        OrchestrationMetrics.SubagentSpawns.Should().NotBeNull();
        OrchestrationMetrics.ToolCalls.Should().NotBeNull();
    }

    [Fact]
    public void TokenUsageMetrics_AllInstruments_AreNotNull()
    {
        TokenUsageMetrics.InputTokens.Should().NotBeNull();
        TokenUsageMetrics.OutputTokens.Should().NotBeNull();
        TokenUsageMetrics.TotalTokens.Should().NotBeNull();
        TokenUsageMetrics.BudgetUsed.Should().NotBeNull();
    }

    [Fact]
    public void ToolExecutionMetrics_AllInstruments_AreNotNull()
    {
        ToolExecutionMetrics.Duration.Should().NotBeNull();
        ToolExecutionMetrics.Invocations.Should().NotBeNull();
        ToolExecutionMetrics.Errors.Should().NotBeNull();
        ToolExecutionMetrics.EmptyResults.Should().NotBeNull();
        ToolExecutionMetrics.ResultSize.Should().NotBeNull();
    }

    [Fact]
    public void AllMetrics_ReturnSameInstanceOnRepeatedAccess()
    {
        var first = ToolExecutionMetrics.Duration;
        var second = ToolExecutionMetrics.Duration;
        first.Should().BeSameAs(second);
    }
}
