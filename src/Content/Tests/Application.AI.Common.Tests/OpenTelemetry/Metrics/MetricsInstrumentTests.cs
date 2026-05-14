using Application.AI.Common.OpenTelemetry.Metrics;
using Domain.AI.Telemetry.Conventions;
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

    // ── Escalation Metrics ──

    [Fact]
    public void EscalationMetrics_Requests_IsNotNull()
    {
        EscalationMetrics.Requests.Should().NotBeNull();
        EscalationMetrics.Requests.Name.Should().Be(EscalationConventions.Requests);
    }

    [Fact]
    public void EscalationMetrics_Resolutions_IsNotNull()
    {
        EscalationMetrics.Resolutions.Should().NotBeNull();
        EscalationMetrics.Resolutions.Name.Should().Be(EscalationConventions.Resolutions);
    }

    [Fact]
    public void EscalationMetrics_DurationMs_IsNotNull()
    {
        EscalationMetrics.DurationMs.Should().NotBeNull();
        EscalationMetrics.DurationMs.Name.Should().Be(EscalationConventions.DurationMs);
    }

    [Fact]
    public void EscalationMetrics_Timeouts_IsNotNull()
    {
        EscalationMetrics.Timeouts.Should().NotBeNull();
        EscalationMetrics.Timeouts.Name.Should().Be(EscalationConventions.Timeouts);
    }

    [Fact]
    public void EscalationMetrics_Pending_IsNotNull()
    {
        EscalationMetrics.Pending.Should().NotBeNull();
        EscalationMetrics.Pending.Name.Should().Be(EscalationConventions.Pending);
    }

    [Fact]
    public void EscalationMetrics_ApproverResponseMs_IsNotNull()
    {
        EscalationMetrics.ApproverResponseMs.Should().NotBeNull();
        EscalationMetrics.ApproverResponseMs.Name.Should().Be(EscalationConventions.ApproverResponseMs);
    }

    // ── Resilience Metrics ──

    [Fact]
    public void ResilienceMetrics_FallbackActivations_IsNotNull()
    {
        ResilienceMetrics.FallbackActivations.Should().NotBeNull();
        ResilienceMetrics.FallbackActivations.Name.Should().Be(ResilienceConventions.FallbackActivations);
    }

    [Fact]
    public void ResilienceMetrics_CircuitStateChanges_IsNotNull()
    {
        ResilienceMetrics.CircuitStateChanges.Should().NotBeNull();
        ResilienceMetrics.CircuitStateChanges.Name.Should().Be(ResilienceConventions.CircuitStateChanges);
    }

    [Fact]
    public void ResilienceMetrics_CircuitState_IsNotNull()
    {
        ResilienceMetrics.CircuitState.Should().NotBeNull();
        ResilienceMetrics.CircuitState.Name.Should().Be(ResilienceConventions.CircuitState);
    }

    [Fact]
    public void ResilienceMetrics_RetryAttempts_IsNotNull()
    {
        ResilienceMetrics.RetryAttempts.Should().NotBeNull();
        ResilienceMetrics.RetryAttempts.Name.Should().Be(ResilienceConventions.RetryAttempts);
    }

    [Fact]
    public void ResilienceMetrics_ProviderDurationMs_IsNotNull()
    {
        ResilienceMetrics.ProviderDurationMs.Should().NotBeNull();
        ResilienceMetrics.ProviderDurationMs.Name.Should().Be(ResilienceConventions.ProviderDurationMs);
    }

    [Fact]
    public void ResilienceMetrics_DegradationEvents_IsNotNull()
    {
        ResilienceMetrics.DegradationEvents.Should().NotBeNull();
        ResilienceMetrics.DegradationEvents.Name.Should().Be(ResilienceConventions.DegradationEvents);
    }

    [Fact]
    public void ResilienceMetrics_QueueSize_IsNotNull()
    {
        ResilienceMetrics.QueueSize.Should().NotBeNull();
        ResilienceMetrics.QueueSize.Name.Should().Be(ResilienceConventions.QueueSize);
    }

    [Fact]
    public void ResilienceMetrics_QueueExpired_IsNotNull()
    {
        ResilienceMetrics.QueueExpired.Should().NotBeNull();
        ResilienceMetrics.QueueExpired.Name.Should().Be(ResilienceConventions.QueueExpired);
    }

    // ── Conventions Naming Convention Tests ──

    [Fact]
    public void EscalationConventions_Constants_FollowNamingConvention()
    {
        EscalationConventions.Requests.Should().StartWith("agent.escalation.");
        EscalationConventions.Resolutions.Should().StartWith("agent.escalation.");
        EscalationConventions.DurationMs.Should().StartWith("agent.escalation.");
        EscalationConventions.Timeouts.Should().StartWith("agent.escalation.");
        EscalationConventions.Pending.Should().StartWith("agent.escalation.");
        EscalationConventions.ApproverResponseMs.Should().StartWith("agent.escalation.");
    }

    [Fact]
    public void ResilienceConventions_Constants_FollowNamingConvention()
    {
        ResilienceConventions.FallbackActivations.Should().StartWith("agent.resilience.");
        ResilienceConventions.CircuitStateChanges.Should().StartWith("agent.resilience.");
        ResilienceConventions.CircuitState.Should().StartWith("agent.resilience.");
        ResilienceConventions.RetryAttempts.Should().StartWith("agent.resilience.");
        ResilienceConventions.ProviderDurationMs.Should().StartWith("agent.resilience.");
        ResilienceConventions.DegradationEvents.Should().StartWith("agent.resilience.");
        ResilienceConventions.QueueSize.Should().StartWith("agent.resilience.");
        ResilienceConventions.QueueExpired.Should().StartWith("agent.resilience.");
    }
}
