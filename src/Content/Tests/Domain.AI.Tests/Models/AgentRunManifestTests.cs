using Domain.AI.Models;
using FluentAssertions;
using Xunit;

namespace Domain.AI.Tests.Models;

/// <summary>
/// Tests for <see cref="AgentRunManifest"/>, <see cref="AgentParticipant"/>,
/// <see cref="ToolInvocationSummary"/>, and <see cref="TimingBreakdown"/>.
/// </summary>
public sealed class AgentRunManifestTests
{
    [Fact]
    public void Defaults_AllCollections_AreEmpty()
    {
        var manifest = new AgentRunManifest
        {
            RunId = "run-1",
            StartedAt = DateTimeOffset.UtcNow
        };

        manifest.Agents.Should().BeEmpty();
        manifest.ToolInvocations.Should().BeEmpty();
        manifest.McpServersUsed.Should().BeEmpty();
        manifest.TurnCount.Should().Be(0);
        manifest.ContentSafetyBlocks.Should().Be(0);
        manifest.Timing.Should().BeNull();
    }

    [Fact]
    public void AllProperties_SetExplicitly_RetainValues()
    {
        var agents = new List<AgentParticipant>
        {
            new() { AgentId = "orchestrator", TokensUsed = 5000, TurnCount = 3 }
        };
        var tools = new List<ToolInvocationSummary>
        {
            new() { ToolName = "bash", InvocationCount = 2, SuccessCount = 2 }
        };
        var timing = new TimingBreakdown
        {
            ContextAssembly = TimeSpan.FromMilliseconds(100),
            Generation = TimeSpan.FromMilliseconds(500),
            ToolExecution = TimeSpan.FromMilliseconds(200),
            Total = TimeSpan.FromMilliseconds(800)
        };

        var manifest = new AgentRunManifest
        {
            RunId = "run-2",
            StartedAt = DateTimeOffset.UtcNow,
            Agents = agents,
            ToolInvocations = tools,
            TurnCount = 5,
            ContentSafetyBlocks = 1,
            McpServersUsed = ["context7", "firecrawl"],
            Timing = timing
        };

        manifest.Agents.Should().HaveCount(1);
        manifest.ToolInvocations.Should().HaveCount(1);
        manifest.TurnCount.Should().Be(5);
        manifest.ContentSafetyBlocks.Should().Be(1);
        manifest.McpServersUsed.Should().HaveCount(2);
        manifest.Timing.Should().NotBeNull();
    }

    [Fact]
    public void AgentParticipant_ParentAgentId_DefaultsToNull()
    {
        var participant = new AgentParticipant { AgentId = "worker" };

        participant.ParentAgentId.Should().BeNull();
        participant.TokensUsed.Should().Be(0);
        participant.TurnCount.Should().Be(0);
    }

    [Fact]
    public void ToolInvocationSummary_Defaults_AreZero()
    {
        var summary = new ToolInvocationSummary { ToolName = "bash" };

        summary.InvocationCount.Should().Be(0);
        summary.TotalDuration.Should().Be(TimeSpan.Zero);
        summary.SuccessCount.Should().Be(0);
        summary.FailureCount.Should().Be(0);
    }

    [Fact]
    public void TimingBreakdown_Defaults_AreZero()
    {
        var timing = new TimingBreakdown();

        timing.ContextAssembly.Should().Be(TimeSpan.Zero);
        timing.Generation.Should().Be(TimeSpan.Zero);
        timing.ToolExecution.Should().Be(TimeSpan.Zero);
        timing.Total.Should().Be(TimeSpan.Zero);
    }
}
